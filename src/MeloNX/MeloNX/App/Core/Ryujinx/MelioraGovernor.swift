//
//  MelioraGovernor.swift
//  Meliora
//
//  While a game runs this watches how much memory iOS still lets the app use and
//  tells the emulator core, so its caches shrink early instead of the app being
//  terminated. It also logs memory and thermal state (to find out why a game
//  stutters) and makes sure the pipeline cache is saved when the app leaves the
//  foreground.
//

import Foundation
import UIKit
import os

final class MelioraGovernor {
    static let shared = MelioraGovernor()

    /// UserDefaults key of the "Memory Governor" setting. The default is ON.
    static let enabledKey = "melioraMemoryGovernor"

    private let queue = DispatchQueue(label: "app.meliora.governor", qos: .utility)
    private var timer: DispatchSourceTimer?
    private var observers: [NSObjectProtocol] = []
    private var running = false
    private var tick = 0
    private var headroom: Int64 = 768 * 1024 * 1024
    private var lastTrim = Date.distantPast

    // FPS samples for the 10-second log window (accessed on the governor queue only).
    private var fpsSum = 0.0
    private var fpsMin = Double.greatestFiniteMagnitude
    private var fpsCount = 0

    private let tickInterval: TimeInterval = 0.5
    private let logEveryTicks = 20        // every 10 seconds
    private let saveEveryTicks = 1200     // every 10 minutes

    private init() {}

    private var isEnabled: Bool {
        UserDefaults.standard.object(forKey: MelioraGovernor.enabledKey) as? Bool ?? true
    }

    // MARK: - Lifecycle

    /// Starts watching. Safe to call more than once.
    func start() {
        queue.async { [weak self] in
            self?.startOnQueue()
        }
    }

    /// Stops watching.
    func stop() {
        queue.async { [weak self] in
            self?.stopOnQueue()
        }
    }

    private func startOnQueue() {
        fpsSum = 0
        fpsMin = Double.greatestFiniteMagnitude
        fpsCount = 0

        guard !running else {
            // A new game started in the same app session: apply the current setting again.
            SN_meliora_memory_configure(isEnabled ? 1 : 0, headroom)
            reportAvailableMemory()
            return
        }

        running = true
        tick = 0

        // Keep roughly 10% of physical memory free, between 512 MB and 1 GB.
        let megabyte = 1024.0 * 1024.0
        let physical = Double(ProcessInfo.processInfo.physicalMemory)
        headroom = Int64(min(max(physical * 0.10, 512.0 * megabyte), 1024.0 * megabyte))

        SN_meliora_memory_configure(isEnabled ? 1 : 0, headroom)
        print("[Meliora] governor \(isEnabled ? "on" : "off"), physical \(Int(physical / megabyte)) MB, headroom \(headroom / 1_048_576) MB")
        reportAvailableMemory()

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + tickInterval, repeating: tickInterval, leeway: .milliseconds(100))
        timer.setEventHandler { [weak self] in
            self?.onTick()
        }
        timer.resume()
        self.timer = timer

        let center = NotificationCenter.default

        observers.append(center.addObserver(forName: UIApplication.didReceiveMemoryWarningNotification, object: nil, queue: nil) { [weak self] _ in
            self?.queue.async {
                self?.handleMemoryWarning()
            }
        })

        observers.append(center.addObserver(forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: nil) { _ in
            let state = MelioraGovernor.describe(ProcessInfo.processInfo.thermalState)
            print("[Meliora] thermal state changed: \(state)")
        })

        observers.append(center.addObserver(forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: nil) { [weak self] _ in
            self?.saveForBackground()
        })
    }

    private func stopOnQueue() {
        guard running else { return }
        running = false

        timer?.cancel()
        timer = nil

        for observer in observers {
            NotificationCenter.default.removeObserver(observer)
        }
        observers.removeAll()
    }

    // MARK: - Periodic work

    private func onTick() {
        tick += 1

        let available = Int64(os_proc_available_memory())

        // Sample FPS on every tick so logStatus can report min/avg over its window.
        let currentFps = Double(SN_get_current_fps())
        fpsSum += currentFps
        fpsCount += 1
        if currentFps < fpsMin {
            fpsMin = currentFps
        }

        if isEnabled && available > 0 {
            SN_meliora_memory_report(available)

            if available < headroom / 2 && Date().timeIntervalSince(lastTrim) > 10 {
                lastTrim = Date()
                print("[Meliora] low memory (\(available / 1_048_576) MB available), trimming caches")
                SN_meliora_memory_trim()
            }
        }

        if tick % logEveryTicks == 0 {
            logStatus(available: available)
        }

        if tick % saveEveryTicks == 0 {
            SN_meliora_save_caches(0)
        }
    }

    /// Sends the current amount of available memory to the core (the core stays conservative until it has one).
    private func reportAvailableMemory() {
        let available = Int64(os_proc_available_memory())

        if isEnabled && available > 0 {
            SN_meliora_memory_report(available)
        }
    }

    private func handleMemoryWarning() {
        print("[Meliora] iOS memory warning received")

        if isEnabled {
            lastTrim = Date()
            SN_meliora_memory_trim()
        }
    }

    private func logStatus(available: Int64) {
        let footprint = MelioraGovernor.physicalFootprint()
        let thermal = MelioraGovernor.describe(ProcessInfo.processInfo.thermalState)
        let lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
        let fps = SN_get_current_fps()

        let avgFps = fpsCount > 0 ? fpsSum / Double(fpsCount) : 0
        let minFps = fpsCount > 0 ? fpsMin : 0

        print("[Meliora] mem footprint=\(footprint / 1_048_576)MB available=\(available / 1_048_576)MB thermal=\(thermal) lowPower=\(lowPower) fps=\(fps) avgFps=\(String(format: "%.1f", avgFps)) minFps=\(String(format: "%.1f", minFps))")

        // Start a new 10-second window.
        fpsSum = 0
        fpsMin = Double.greatestFiniteMagnitude
        fpsCount = 0
    }

    // MARK: - Background

    private func saveForBackground() {
        let background = BackgroundTaskBox()

        background.identifier = UIApplication.shared.beginBackgroundTask(withName: "MelioraSaveCaches") {
            background.end()
        }

        queue.async {
            SN_meliora_save_caches(1)
            background.end()
        }
    }

    // MARK: - Helpers

    private static func describe(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "unknown"
        }
    }

    private static func physicalFootprint() -> UInt64 {
        var taskInfo = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.stride) / 4

        let result: kern_return_t = withUnsafeMutablePointer(to: &taskInfo) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }

        return result == KERN_SUCCESS ? taskInfo.phys_footprint : 0
    }
}

/// Keeps a background task identifier so it can be ended exactly once from any thread.
private final class BackgroundTaskBox {
    private let lock = NSLock()
    private var current: UIBackgroundTaskIdentifier = .invalid

    var identifier: UIBackgroundTaskIdentifier {
        get {
            lock.lock()
            defer { lock.unlock() }
            return current
        }
        set {
            lock.lock()
            current = newValue
            lock.unlock()
        }
    }

    func end() {
        lock.lock()
        let task = current
        current = .invalid
        lock.unlock()

        guard task != .invalid else { return }

        DispatchQueue.main.async {
            UIApplication.shared.endBackgroundTask(task)
        }
    }
}
