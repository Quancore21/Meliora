//
//  LaunchGameHandler.swift
//  MeloNX
//
//  Created by Stossy11 on 10/11/2025.
//

import Combine
import Foundation
import SwiftUI

class LaunchGameHandler: ObservableObject {
    @Published var currentGame: Game? = nil
    @Published var profileSelected = false
    @Published var showApp: Bool = true
    @Published var isPortrait: Bool = true
    @Published var startError: String? = nil
    @AppStorage("gametorun") var gametorun: String = ""
    @AppStorage("gametorun-date") var gametorunDate: String = ""
    
    static var succeededJIT: Bool = true
    
    private static let jitEntitlement = "com.apple.developer.kernel.increased-memory-limit"
    
    private let ryujinx = Ryujinx.shared
    private let nativeSettings = NativeSettingsManager.shared
    private let settingsManager = SettingsManager.shared
    private let persettings = PerGameSettingsManager.shared
    private let controllerManager = ControllerManager.shared
    
    private var config: Ryujinx.Arguments {
        settingsManager.config
    }
    
    private var hasJITEntitlement: Bool {
        ProcessInfo.processInfo.isiOSAppOnMac ? true : checkAppEntitlement(Self.jitEntitlement)
    }
    
    var isGameReady: Bool {
        currentGame != nil
            && (nativeSettings.ignoreJIT.value ? true : ryujinx.jitenabled)
            && (nativeSettings.showProfileonGame.value ? profileSelected : true)
    }
    
    var shouldLaunchGame: Bool {
        isGameReady && hasJITEntitlement
    }
    
    var shouldShowEntitlement: Bool {
        isGameReady && !hasJITEntitlement
    }
    
    var shouldShowPopover: Bool {
        currentGame != nil
            && ryujinx.jitenabled
            && !profileSelected
            && nativeSettings.showProfileonGame.value
            && hasJITEntitlement
    }
    
    var shouldCheckJIT: Bool {
        currentGame != nil
            && !ryujinx.jitenabled
            && !(nativeSettings.ignoreJIT.value as Bool)
            && hasJITEntitlement
    }
    
    
    
    func enableJIT() {
        ryujinx.checkForJIT()
        print("Has TXM? \(ProcessInfo.processInfo.hasTXM)")
        
        if !ryujinx.jitenabled {
            if nativeSettings.useTrollStore.value {
                gametorunDate = "\(Date().timeIntervalSince1970)"
                gametorun = currentGame?.titleId ?? ""
                askForJIT()
            } else if nativeSettings.stikJIT.value {
                gametorunDate = "\(Date().timeIntervalSince1970)"
                gametorun = currentGame?.titleId ?? ""
                enableJITStik()
            } else {
                // nothing
            }
        }
    }
    
    func startGame() {
        enableJIT()
        
        // enableJIT hands off to TrollStore/StikJIT and relaunches the app with
        // `gametorun` set. Trying to start emulation on this pass would run
        // without a JIT, so bail out and wait for the relaunch.
        if !ryujinx.jitenabled, !(nativeSettings.ignoreJIT.value as Bool), !gametorun.isEmpty {
            return
        }
        
        MusicSelectorView.stopMusic()
        nativeSettings.isVirtualController.value = controllerManager.hasVirtualController()
        MetalView.createView()
        
        guard let currentGame else { return }
        
        persettings.loadSettings()
        
        let config = persettings.config[currentGame.titleId] ?? self.config
        
        controllerManager.registerControllerTypeForMatchingControllers()
        config.gamepath = currentGame.fileURL.path
        config.inputids = Array(Set(controllerManager.selectedControllers))
        
        if config.inputids.isEmpty {
            config.inputids.append("0")
        }
        
        print(config.inputids)
        configureEnvironmentVariables()
        
        do {
            try ryujinx.start(with: config)
        } catch {
            print("Failed to start game '\(currentGame.titleId)': \(error)")
            
            // A silent print is invisible to the user; surface the failure.
            startError = "Failed to start '\(currentGame.titleName)': \(error.localizedDescription)"
        }
    }
    
    private func configureEnvironmentVariables() {
        // Meliora experimental toggles (UserDefaults); also applied in
        // MeloNXApp.setupEnvironment() before MoltenVK first initializes.
        let threadQoS = UserDefaults.standard.object(forKey: "melioraThreadQoS") as? Bool ?? true
        setenv("MELIORA_QOS", threadQoS ? "1" : "0", 1)

        // Meliora Adaptive Engine: closed-loop render scale, pacing target and live pacing.
        let melioraAdaptive = UserDefaults.standard.object(forKey: "melioraAdaptive") as? Bool ?? true
        setenv("MELIORA_ADAPTIVE", melioraAdaptive ? "1" : "0", 1)

        let target30 = UserDefaults.standard.bool(forKey: "melioraTarget30")
        setenv("MELIORA_TARGET_FPS", target30 ? "30" : "60", 1)

        let melioraSmooth = UserDefaults.standard.bool(forKey: "melioraSmooth")
        setenv("MELIORA_SMOOTH", melioraSmooth ? "1" : "0", 1)

        let mvkPrefill = UserDefaults.standard.bool(forKey: "melioraMvkPrefillCmdBuffers")
        setenv("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS", mvkPrefill ? "1" : "0", 1)

        let mvkCompression = UserDefaults.standard.bool(forKey: "melioraMvkShaderCompression")
        setenv("MVK_CONFIG_SHADER_COMPRESSION_ALGORITHM", mvkCompression ? "4" : "0", 1)

        let useDualMappedJIT: Bool
        if #available(iOS 19, *) {
            useDualMappedJIT = nativeSettings.setting(forKey: "DUAL_MAPPED_JIT", default: true).value
        } else {
            useDualMappedJIT = nativeSettings.setting(forKey: "DUAL_MAPPED_JIT", default: false).value
        }
        
        if useDualMappedJIT {
            setenv("DUAL_MAPPED_JIT", "1", 1)
            Self.succeededJIT = RyujinxBridge.initialize_dualmapped()
        } else {
            setenv("DUAL_MAPPED_JIT", "0", 1)
        }
        
        guard let device = MTLCreateSystemDefaultDevice() else {
            setenv("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", "1", 1)
            return
        }
        
        let supportsArgumentBuffersTier2 = device.argumentBuffersSupport.rawValue >= MTLArgumentBuffersTier.tier2.rawValue
        setenv("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", supportsArgumentBuffersTier2 ? "1" : "0", 1)
    }
}
