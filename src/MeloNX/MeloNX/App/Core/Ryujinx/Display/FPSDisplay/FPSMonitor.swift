//
//  FPSMonitor.swift
//  MeloNX
//
//  Created by Stossy11 on 21/12/2024.
//

import Foundation
import Combine

@MainActor
class FPSMonitor: ObservableObject {
    @Published private(set) var currentFPS: UInt64 = 0
    private var task: Task<Void, Never>?

    init() {
        // The loop lives inside the task and re-binds `self` on every tick, so a
        // released monitor is not kept alive by its own polling task.
        task = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }

                self.currentFPS = UInt64(RyujinxBridge.currentFPS)

                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        }
    }

    deinit {
        task?.cancel()
    }

    func formatFPS() -> String {
        String(format: "FPS: %.2f", Double(currentFPS))
    }
}
