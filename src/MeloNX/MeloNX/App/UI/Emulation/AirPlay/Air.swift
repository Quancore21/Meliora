// Credit https://github.com/heestand-xyz/AirKit

import UIKit
import SwiftUI

public class Air {
    
    static let shared = Air()
    
    public var connected: Bool = false {
        didSet {
            let callbacks: [UUID: (Bool) -> ()]

            callbacksLock.lock()
            callbacks = connectionCallbacks
            callbacksLock.unlock()

            callbacks.values.forEach({ $0(connected) })
        }
    }

    // Callbacks are keyed by token so a removed view can stop receiving them.
    // Previously they were appended and never removed, which kept every
    // EmulationView (and everything it owns) alive for the whole process lifetime.
    private var connectionCallbacks: [UUID: (Bool) -> ()] = [:]
    private let callbacksLock = NSLock()

    /// Registers a connection callback and returns a token used to unregister it.
    @discardableResult
    static func addConnectionCallback(_ callback: @escaping (Bool) -> ()) -> UUID {
        let token = UUID()

        Air.shared.callbacksLock.lock()
        Air.shared.connectionCallbacks[token] = callback
        Air.shared.callbacksLock.unlock()

        // Deliver the current state right away so the caller does not miss it.
        callback(Air.shared.connected)

        return token
    }

    static func removeConnectionCallback(_ token: UUID) {
        Air.shared.callbacksLock.lock()
        Air.shared.connectionCallbacks.removeValue(forKey: token)
        Air.shared.callbacksLock.unlock()
    }
    
    var airScreen: UIScreen?
    var airWindow: UIWindow?
    
    var hostingController: UIHostingController<AnyView>?
    
    var appIsActive: Bool { UIApplication.shared.applicationState == .active }
    
    private var observers: [NSObjectProtocol] = []

    init() {
        observers.append(NotificationCenter.default.addObserver(forName: UIScreen.didConnectNotification, object: nil, queue: .main) { [weak self] note in
            self?.didConnect(sender: note)
        })
        observers.append(NotificationCenter.default.addObserver(forName: UIScreen.didDisconnectNotification, object: nil, queue: .main) { [weak self] _ in
            self?.didDisconnect()
        })
        observers.append(NotificationCenter.default.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.didBecomeActive()
        })
        observers.append(NotificationCenter.default.addObserver(forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.willResignActive()
        })
    }

    deinit {
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }
    
    private func check() {
       if let connectedScreen = UIScreen.screens.first(where: { $0 != .main }) {
            add(screen: connectedScreen) { success in
                guard success else { return }
                self.connected = true
            }
        }
    }
    
    public static func play(_ view: AnyView) {
        Air.shared.hostingController = UIHostingController<AnyView>(rootView: view)
        Air.shared.check()
    }
    
    public static func stop() {
        Air.shared.remove()
        Air.shared.hostingController = nil
    }
    
    @objc func didConnect(sender: Notification) {
        // print("AirKit - Connect")
        self.connected = true
        guard let screen: UIScreen = sender.object as? UIScreen else { return }
        add(screen: screen) { success in
            guard success else { return }
            self.connected = true
        }
    }
    
    func add(screen: UIScreen, completion: @escaping (Bool) -> ()) {
        
        // print("AirKit - Add Screen")
        
        airScreen = screen
        
        airWindow = UIWindow(frame: airScreen!.bounds)
        
        guard let viewController: UIViewController = hostingController else {
            // print("AirKit - Add - Failed: Hosting Controller Not Found")
            completion(false)
            return
        }
        
        findWindowScene(for: airScreen!) { windowScene in
            guard let airWindowScene: UIWindowScene = windowScene else {
                // print("AirKit - Add - Failed: Window Scene Not Found")
                completion(false)
                return
            }
            self.airWindow?.rootViewController = viewController
            self.airWindow?.windowScene = airWindowScene
            
            if let _ = viewController as? UIHostingController<AnyView> {
                let traitCollection = UITraitCollection(traitsFrom: [
                    UITraitCollection(userInterfaceIdiom: .tv),
                    airWindowScene.traitCollection
                ])
                viewController.setOverrideTraitCollection(traitCollection, forChild: viewController)
            }
            
            self.airWindow?.isHidden = false
            // print("AirKit - Add Screen - Done")
            completion(true)
        }
        
    }
    
    func findWindowScene(for screen: UIScreen, shouldRecurse: Bool = true, completion: @escaping (UIWindowScene?) -> ())  {
        // print("AirKit - Find Window Scene")
        var matchingWindowScene: UIWindowScene? = nil
        let scenes = UIApplication.shared.connectedScenes
        for scene in scenes {
            if let windowScene = scene as? UIWindowScene {
                if windowScene.screen == screen {
                    matchingWindowScene = windowScene
                    break
                }
            }
        }
        guard let windowScene: UIWindowScene = matchingWindowScene else {
            // Only recurse once to avoid infinite loops
            if shouldRecurse {
               Task { @MainActor in
                    self.findWindowScene(for: screen, shouldRecurse: false) { windowScene in
                        completion(windowScene)
                    }
                }
            } else {
                completion(nil)
            }
            return
        }
        completion(windowScene)
    }
    
    @objc func didDisconnect() {
        // print("AirKit - Disconnect")
        remove()
        connected = false
    }
    
    func remove() {
        // print("AirKit - Remove")
        airWindow = nil
        airScreen = nil
    }
    
    @objc func didBecomeActive() {
        // print("AirKit - App Active")
    }
    
    @objc func willResignActive() {
        // print("AirKit - App Inactive")
        
    }
    
}
