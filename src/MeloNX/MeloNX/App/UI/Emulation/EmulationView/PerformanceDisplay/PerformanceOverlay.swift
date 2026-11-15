//
//  Untitled.swift
//  MeloNX
//
//  Created by Stossy11 on 21/12/2024.
//

import SwiftUI

struct PerformanceOverlayView: View  {
    @StateObject private var memorymonitor = MemoryUsageMonitor()
    
    @StateObject private var fpsmonitor = FPSMonitor()
    @State private var batteryLevel: Int = Int(UIDevice.current.batteryLevel * 100)
    
    @AppStorage("showBatteryPercentage") var showBatteryPercentage: Bool = false
    
    @AppStorage("horizontalorvertical") var horizontalorvertical: Bool = false

    @State private var batteryObserver: NSObjectProtocol?
    
    @ViewBuilder
    var content: some View {
        if horizontalorvertical {
            HStack(spacing: 8) {
                if showBatteryPercentage {
                    Text("Battery: \(batteryLevel)%")
                        .foregroundStyle(.white)
                }
                Text("\(fpsmonitor.formatFPS())")
                    .foregroundStyle(.white)
                Text("RAM: " + memorymonitor.formatMemorySize(memorymonitor.memoryUsage))
                    .foregroundStyle(.white)
            }
            .padding(10)
        } else {
            VStack(alignment: .trailing, spacing: 8) {
                if showBatteryPercentage {
                    Text("Battery: \(batteryLevel)%")
                        .foregroundStyle(.white)
                }
                Text("\(fpsmonitor.formatFPS())")
                    .foregroundStyle(.white)
                Text("RAM: " + memorymonitor.formatMemorySize(memorymonitor.memoryUsage))
                    .foregroundStyle(.white)
            }
            .padding(10)
            .frame(minWidth: 150)
        }
    }
    
    var body: some View {
        Group {
            if #available(iOS 19.0, *), !NativeSettingsManager.shared.disableLiquidGlass.value {
                GlassEffectContainer {
                    content
                        .glassEffect(.clear.tint(.black.opacity(0.6)),in: RoundedRectangle(cornerRadius: 5))
                }
            } else {
                content
                    .background(Color.black.opacity(0.7))
            }
        }
        .onAppear() {
            UIDevice.current.isBatteryMonitoringEnabled = true
            batteryLevel = Int(UIDevice.current.batteryLevel * 100)

            // Register only once; without this every re-render of the overlay
            // leaves another observer behind.
            guard batteryObserver == nil else { return }

            batteryObserver = NotificationCenter.default.addObserver(
                forName: UIDevice.batteryLevelDidChangeNotification,
                object: nil,
                queue: .main
            ) { _ in
                batteryLevel = Int(UIDevice.current.batteryLevel * 100)
            }
        }
        .onDisappear() {
            if let batteryObserver {
                NotificationCenter.default.removeObserver(batteryObserver)
                self.batteryObserver = nil
            }
        }
    }
}

