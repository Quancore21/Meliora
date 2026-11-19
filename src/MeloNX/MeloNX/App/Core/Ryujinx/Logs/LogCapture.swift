//
//  LogCapture.swift
//  MeloNX
//
//  Created by Stossy11 on 22/09/2025.
//


import Foundation

final class LogCapture: ObservableObject {
    static let shared = LogCapture()

    /// Keeps memory bounded on long sessions; only the tail is used for crash reports.
    private static let maxCapturedLogs = 4000

    private static let logLineRegex = try? NSRegularExpression(
        pattern: "\\d{2}:\\d{2}:\\d{2}\\.\\d{3} \\|[A-Z]+\\|",
        options: .caseInsensitive
    )

    private var stdoutPipe: Pipe?
    private var stderrPipe: Pipe?
    private let originalStdout: Int32
    private let originalStderr: Int32

    // The readability handlers run on background queues while the UI and the
    // crash reporter read the captured logs from the main thread, so all shared
    // state is guarded by this lock.
    private let stateLock = NSLock()
    private var continuation: AsyncStream<String>.Continuation?
    private var storedLogs: [String] = []

    // A pipe read can end in the middle of a line. Keep the incomplete tail here
    // until the next chunk arrives; splitting every chunk independently used to
    // merge the tail with the next line and corrupt the exported log.
    private var stdoutRemainder = Data()
    private var stderrRemainder = Data()

    public var capturedLogs: [String] {
        stateLock.lock()
        defer { stateLock.unlock() }

        return storedLogs
    }

    func snapshotLogs() -> [String] {
        capturedLogs
    }

    lazy var logs: AsyncStream<String> = {
        AsyncStream { continuation in
            self.stateLock.lock()
            self.continuation = continuation
            self.stateLock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }

                self.stateLock.lock()
                self.continuation = nil
                self.stateLock.unlock()
            }
        }
    }()

    private init() {
        originalStdout = dup(STDOUT_FILENO)
        originalStderr = dup(STDERR_FILENO)
        startCapturing()
    }

    func startCapturing() {
        guard stdoutPipe == nil, stderrPipe == nil else { return }

        let outPipe = Pipe()
        let errPipe = Pipe()

        stdoutPipe = outPipe
        stderrPipe = errPipe

        redirectOutput(to: outPipe, fileDescriptor: STDOUT_FILENO)
        redirectOutput(to: errPipe, fileDescriptor: STDERR_FILENO)

        setupReadabilityHandler(for: outPipe, isStdout: true)
        setupReadabilityHandler(for: errPipe, isStdout: false)
    }

    func stopCapturing() {
        stateLock.lock()
        let outPipe = stdoutPipe
        let errPipe = stderrPipe
        stdoutPipe = nil
        stderrPipe = nil
        stateLock.unlock()

        guard outPipe != nil || errPipe != nil else { return }

        flushRemainders()

        dup2(originalStdout, STDOUT_FILENO)
        dup2(originalStderr, STDERR_FILENO)

        outPipe?.fileHandleForReading.readabilityHandler = nil
        errPipe?.fileHandleForReading.readabilityHandler = nil
    }

    private func redirectOutput(to pipe: Pipe, fileDescriptor: Int32) {
        dup2(pipe.fileHandleForWriting.fileDescriptor, fileDescriptor)
    }

    private func setupReadabilityHandler(for pipe: Pipe, isStdout: Bool) {
        pipe.fileHandleForReading.readabilityHandler = { [weak self] fileHandle in
            guard let self else { return }

            let data = fileHandle.availableData
            let originalFD = isStdout ? self.originalStdout : self.originalStderr

            // Extract first, then tee: originalStdout/originalStderr point at the
            // on-disk log, so writing the raw chunk there would preserve the very
            // split/merged lines this buffer is meant to remove.
            let complete = self.extractCompleteLines(data, isStdout: isStdout)

            if !complete.isEmpty {
                write(originalFD, (complete as NSData).bytes, complete.count)
            }

            guard !complete.isEmpty,
                  let logString = String(data: complete, encoding: .utf8) else { return }

            let cleanedLog = self.cleanLog(logString)

            // The exported log keeps every complete line; the live stream is filtered
            // only for display. The old code dropped a whole chunk when its filtered
            // form was empty, which could hide `[Meliora]` lines that shared a chunk
            // with no matching C# line.
            if !cleanedLog.all.isEmpty {
                self.appendLog(cleanedLog.all)
            }

            guard !cleanedLog.filtered.isEmpty else { return }

            self.stateLock.lock()
            let continuation = self.continuation
            self.stateLock.unlock()

            continuation?.yield(cleanedLog.filtered)
        }
    }

    private func extractCompleteLines(_ data: Data, isStdout: Bool) -> Data {
        stateLock.lock()
        defer { stateLock.unlock() }

        var buffer = isStdout ? stdoutRemainder : stderrRemainder
        buffer.append(data)

        guard let newline = buffer.lastIndex(of: 0x0A) else {
            if isStdout {
                stdoutRemainder = buffer
            } else {
                stderrRemainder = buffer
            }

            return Data()
        }

        let complete = buffer[...newline]
        let remainder = buffer[buffer.index(after: newline)...]

        if isStdout {
            stdoutRemainder = Data(remainder)
        } else {
            stderrRemainder = Data(remainder)
        }

        return Data(complete)
    }

    private func flushRemainders() {
        stateLock.lock()
        let outRemainder = stdoutRemainder
        let errRemainder = stderrRemainder
        stdoutRemainder = Data()
        stderrRemainder = Data()
        stateLock.unlock()

        for (data, fd) in [(outRemainder, originalStdout), (errRemainder, originalStderr)] where !data.isEmpty {
            // Preserve an unterminated tail at shutdown, but only after it has been
            // written as a unit: the file log must not receive a half line either.
            write(fd, (data as NSData).bytes, data.count)

            guard let text = String(data: data, encoding: .utf8) else { continue }

            let cleanedLog = cleanLog(text)

            if !cleanedLog.all.isEmpty {
                appendLog(cleanedLog.all)
            }

            if !cleanedLog.filtered.isEmpty {
                stateLock.lock()
                let continuation = self.continuation
                stateLock.unlock()
                continuation?.yield(cleanedLog.filtered)
            }
        }
    }

    private func appendLog(_ log: String) {
        stateLock.lock()
        defer { stateLock.unlock() }

        storedLogs.append(log)

        if storedLogs.count > Self.maxCapturedLogs {
            storedLogs.removeFirst(storedLogs.count - Self.maxCapturedLogs)
        }
    }

    private func cleanLog(_ raw: String) -> (filtered: String, all: String) {
        let showFullLogs = UserDefaults.standard.bool(forKey: "showFullLogs")
        let lines = raw.split(separator: "\n")

        let filteredLines = lines.filter { line in
            if showFullLogs {
                return true
            }

            let lineString = String(line)
            let matches = Self.logLineRegex?.matches(
                in: lineString,
                options: [],
                range: NSRange(location: 0, length: lineString.utf16.count)
            ) ?? []

            return !matches.isEmpty
        }

        let clean: (Substring) -> String = { line in
            if let tabRange = line.range(of: "\t") {
                return line[tabRange.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            }
            return line.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        let cleaned = filteredLines.map(clean).joined(separator: "\n")
        let cleanedAll = lines.map(clean).joined(separator: "\n")

        return (cleaned.replacingOccurrences(of: "\n\n", with: "\n"), cleanedAll)
    }

    deinit {
        stopCapturing()

        stateLock.lock()
        continuation?.finish()
        continuation = nil
        stateLock.unlock()
    }
}
