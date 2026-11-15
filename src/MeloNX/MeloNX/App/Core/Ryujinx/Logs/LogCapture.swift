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

            if !data.isEmpty {
                write(originalFD, (data as NSData).bytes, data.count)
            }

            guard let logString = String(data: data, encoding: .utf8),
                  let cleanedLog = self.cleanLog(logString),
                  !cleanedLog.0.isEmpty else { return }

            self.appendLog(cleanedLog.1)

            self.stateLock.lock()
            let continuation = self.continuation
            self.stateLock.unlock()

            continuation?.yield(cleanedLog.0)
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

    private func cleanLog(_ raw: String) -> (String, String)? {
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

        let cleaned = filteredLines.map { line -> String in
            if let tabRange = line.range(of: "\t") {
                return line[tabRange.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            }
            return line.trimmingCharacters(in: .whitespacesAndNewlines)
        }.joined(separator: "\n")


        let cleaned2 = lines.map { line -> String in
            if let tabRange = line.range(of: "\t") {
                return line[tabRange.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            }
            return line.trimmingCharacters(in: .whitespacesAndNewlines)
        }.joined(separator: "\n")

        return cleaned.isEmpty ? nil : (cleaned.replacingOccurrences(of: "\n\n", with: "\n"), cleaned2)
    }

    deinit {
        stopCapturing()

        stateLock.lock()
        continuation?.finish()
        continuation = nil
        stateLock.unlock()
    }
}
