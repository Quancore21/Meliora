//
//  MeloNXApp.swift
//  MeloNX
//
//  Created by Stossy11 on 09/11/2025.
//

import SwiftUI

struct EnvironmentVariable: Codable, Hashable {
    let string: String
    var value: String
    
    func set() {
        setenv(string, value, 1)
    }
    
    static func set(_ env: EnvironmentVariable) {
        setenv(env.string, env.value, 1)
    }
}

struct MeloNXApp: View {
    @UIApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @AppStorage("hasbeenfinished") var inSetup: Bool = true
    @AppStorage("skippedSetup") var skippedSetup: Bool = false
    @AppStorage("firstBoot") var firstBoot: Bool = false
    @State var viewShown = false
    @State var showedSetup = false

    
    let environment: [EnvironmentVariable] = [
        EnvironmentVariable(string: "MVK_USE_METAL_PRIVATE_API", value: "1"),
        EnvironmentVariable(string: "MVK_CONFIG_USE_METAL_PRIVATE_API", value: "1"),
        EnvironmentVariable(string: "MVK_DEBUG", value: "0"),
        // EnvironmentVariable(string: "MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS", value: "0"),
        EnvironmentVariable(string: "MVK_CONFIG_MAX_ACTIVE_METAL_COMMAND_BUFFERS_PER_QUEUE", value: "128"),
        // EnvironmentVariable(string: "MVK_CONFIG_SHADER_COMPRESSION_ALGORITHM", value: "4"),
        EnvironmentVariable(string: "DOTNET_DefaultStackSize", value: "200000") // probably doesn't work on NativeAOT
    ]
    
    let fileManager = FileManager.default
    
    init() {
        SDL_SetMainReady()
        SDL_iPhoneSetEventPump(SDL_TRUE)
        SDL_Init(SDL_INIT_EVENTS | SDL_INIT_AUDIO)
        setupEnvironment()
    }
    
    var body: some View {
        Group {
            if !inSetup {
                ContentView(viewShown: $viewShown)
                    .onAppear() {
                        
                        if skippedSetup {
                            return
                        }
                        
                        if !Ryujinx.shared.checkIfKeysImported() {
                            inSetup = true
                        }
                        let firmware = Ryujinx.shared.fetchFirmwareVersion()
                        
                        if (firmware == "" ? "0" : firmware) == "0" {
                            inSetup = true
                        }
                        
                        // NSExtension Test
                        
                        
                    }
            } else {
                SetupView(isInSetup: $inSetup)
                    .onAppear() {
                        let mp3 = MusicSelectorView.getMP3s().first(where: { $0.builtIn })
                        MusicSelectorView.playMusic(mp3)
                        skippedSetup = false
                    }
                    .onDisappear {
                        Timer.scheduledTimer(withTimeInterval: 0.1, repeats: false) { _ in
                            let music = NativeSettingsManager.shared.backgroundMusic("").value
                            MusicSelectorView.stopMusic()
                            if music.isEmpty {
                                if let mp3 = MusicSelectorView.getMP3s().last(where: { $0.builtIn }) {
                                    MusicSelectorView.setMusicItemPath(mp3)
                                    MusicSelectorView.playMusic()
                                }
                            } else {
                                MusicSelectorView.playMusic()
                            }
                        }
                    }
            }
        }
    }
    
    func setupEnvironment() {
        environment.forEach { env in
            env.set()
        }

        // Meliora experimental toggles (UserDefaults). MoltenVK reads its config when it
        // initializes, so these are also re-applied per game launch in LaunchGameHandler.
        let threadQoS = UserDefaults.standard.object(forKey: "melioraThreadQoS") as? Bool ?? true
        setenv("MELIORA_QOS", threadQoS ? "1" : "0", 1)

        // Meliora Adaptive Engine toggles (read by the emulator core at game start).
        let melioraAdaptive = UserDefaults.standard.object(forKey: "melioraAdaptive") as? Bool ?? true
        setenv("MELIORA_ADAPTIVE", melioraAdaptive ? "1" : "0", 1)

        let target30 = UserDefaults.standard.bool(forKey: "melioraTarget30")
        setenv("MELIORA_TARGET_FPS", target30 ? "30" : "60", 1)

        let melioraSmooth = UserDefaults.standard.bool(forKey: "melioraSmooth")
        setenv("MELIORA_SMOOTH", melioraSmooth ? "1" : "0", 1)

        // In MoltenVK "0" means NO_PREFILL, "1" enables prefilling.
        let mvkPrefill = UserDefaults.standard.bool(forKey: "melioraMvkPrefillCmdBuffers")
        setenv("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS", mvkPrefill ? "1" : "0", 1)

        // "4" is ZLIB, "0" is None.
        let mvkCompression = UserDefaults.standard.bool(forKey: "melioraMvkShaderCompression")
        setenv("MVK_CONFIG_SHADER_COMPRESSION_ALGORITHM", mvkCompression ? "4" : "0", 1)

        EnvironmentVariable(string: "HAS_TXM", value: ProcessInfo.processInfo.hasTXM && !ProcessInfo.processInfo.isiOSAppOnMac ? "1" : "0").set()

        RyujinxBridge.initialize()
        
        let cool: Bool
        if #available(iOS 19, *) {
            if ProcessInfo.processInfo.hasTXM {
                NativeSettingsManager.shared.setting(forKey: "DUAL_MAPPED_JIT", default: true).value = true
            }
            
            cool = NativeSettingsManager.shared.setting(forKey: "DUAL_MAPPED_JIT", default: true).value
        } else {
            cool = NativeSettingsManager.shared.setting(forKey: "DUAL_MAPPED_JIT", default: false).value
        }
        
        JIT26BreakpointHandler()
        
        if cool {
            EnvironmentVariable(string: "DUAL_MAPPED_JIT", value: "1").set()
            // The dual-mapped JIT region can only be allocated while a debugger
            // (StikDebug) is attached. At cold start it usually is not yet, so
            // defer allocation to the launch path (LaunchGameHandler), which
            // runs after JIT has been enabled.
            if checkDebugged() {
                LaunchGameHandler.succeededJIT = RyujinxBridge.initialize_dualmapped()
            } else {
                LaunchGameHandler.succeededJIT = false
            }
        } else {
            EnvironmentVariable(string: "DUAL_MAPPED_JIT", value: "0").set()
        }
    }
}


