//
//  MeloMTKView.swift
//  MeloNX
//
//  Created by Stossy11 on 03/03/2025.
//

import MetalKit
import UIKit

class MeloMTKView: MTKView {
    private let maxTouchPoints = 16

    // The index handed to the native touch driver must stay stable for the whole
    // lifetime of a touch. It used to be the position of the touch inside the array,
    // so lifting one finger shifted every other finger's index and made the
    // emulator release the wrong touch point.
    private var activeTouches: [UITouch: Int] = [:]
    private var ignoredTouches: Set<UITouch> = []
    private var usedIndices: Set<Int> = []

    private var aspectRatio: AspectRatio = .fixed16x9

    func setAspectRatio(_ ratio: AspectRatio) {
        self.aspectRatio = ratio
        RyujinxBridge.setViewSize(width: Int(self.bounds.width), height: Int(self.bounds.height))
    }

    private func transformToTargetCoordinates(_ point: CGPoint) -> CGPoint {
        return point
    }

    private func getNextAvailableIndex() -> Int? {
        for index in 0..<maxTouchPoints where !usedIndices.contains(index) {
            return index
        }

        return nil
    }

    private func releaseTouch(_ touch: UITouch) {
        guard let index = activeTouches.removeValue(forKey: touch) else { return }

        usedIndices.remove(index)
        RyujinxBridge.touchEnded(index: index)
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesBegan(touches, with: event)

        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else { return }

        setAspectRatio(Ryujinx.shared.aspectRatio)

        for touch in touches {
            guard let index = getNextAvailableIndex() else {
                ignoredTouches.insert(touch)
                continue
            }

            usedIndices.insert(index)
            activeTouches[touch] = index

            let location = touch.location(in: self)
            let transformed = transformToTargetCoordinates(location)
            RyujinxBridge.touchBegan(x: Float(transformed.x), y: Float(transformed.y), index: index)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesEnded(touches, with: event)

        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else {
            for touch in touches {
                ignoredTouches.remove(touch)
                releaseTouch(touch)
            }
            return
        }

        for touch in touches {
            if ignoredTouches.remove(touch) != nil {
                continue
            }

            releaseTouch(touch)
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesMoved(touches, with: event)

        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else { return }

        setAspectRatio(Ryujinx.shared.aspectRatio)

        for touch in touches {
            if ignoredTouches.contains(touch), activeTouches[touch] == nil {
                continue
            }

            guard let touchIndex = activeTouches[touch] else {
                continue
            }

            let location = touch.location(in: self)
            let transformed = transformToTargetCoordinates(location)
            RyujinxBridge.touchMoved(x: Float(transformed.x), y: Float(transformed.y), index: touchIndex)
        }
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesCancelled(touches, with: event)
        let disabled = UserDefaults.standard.bool(forKey: "disableTouch")
        guard !disabled else {
            for touch in touches {
                ignoredTouches.remove(touch)
                releaseTouch(touch)
            }
            return
        }

        for touch in touches {
            if ignoredTouches.remove(touch) != nil {
                continue
            }

            releaseTouch(touch)
        }
    }

    func resetTouchTracking() {
        for index in usedIndices {
            RyujinxBridge.touchEnded(index: index)
        }

        activeTouches.removeAll()
        ignoredTouches.removeAll()
        usedIndices.removeAll()
    }
}
