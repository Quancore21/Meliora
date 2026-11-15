//
//  IsJITEnabled.swift
//  MeloNX
//
//  Created by Stossy11 on 10/02/2025.
//

import Foundation

@_silgen_name("csops")
func csops(pid: Int32, ops: Int32, useraddr: UnsafeMutableRawPointer?, usersize: Int32) -> Int32

func isJITEnabled() -> Bool {

    if checkAppEntitlement("dynamic-codesigning") {
        return allocateTest()
    }

    // The dual-mapped cache can only be allocated while a debugger (StikDebug)
    // is attached. Before attach, BreakGetJITMapping cannot succeed, so don't
    // poison the session with a failed allocation attempt.
    if checkDebugged() {
        LaunchGameHandler.succeededJIT = RyujinxBridge.initialize_dualmapped()
    } else {
        // succeededJIT defaults to true; don't let a skipped call leak that
        // stale value into the hasTXM path below.
        LaunchGameHandler.succeededJIT = false
    }

    if #available(iOS 19, *) {
        if ProcessInfo.processInfo.hasTXM {
            return LaunchGameHandler.succeededJIT
        }

        return checkDebugged() && LaunchGameHandler.succeededJIT
    } else {
        return checkDebugged() && allocateTest()
    }
}

func checkDebugged() -> Bool {
    var flags: Int = 0
    if checkAppEntitlement("dynamic-codesigning") {
        return true
    }
    return csops(pid: getpid(), ops: 0, useraddr: &flags, usersize: Int32(MemoryLayout.size(ofValue: flags))) == 0 && (flags & Int(CS_DEBUGGED)) != 0
}

func checkMemoryPermissions(at address: UnsafeRawPointer) -> Bool {
    var region: vm_address_t = vm_address_t(UInt(bitPattern: address))
    var regionSize: vm_size_t = 0
    var info = vm_region_basic_info_64()
    var infoCount = mach_msg_type_number_t(MemoryLayout<vm_region_basic_info_64>.size / MemoryLayout<integer_t>.size)
    var objectName: mach_port_t = UInt32(MACH_PORT_NULL)
    
    let result = withUnsafeMutablePointer(to: &info) {
        $0.withMemoryRebound(to: integer_t.self, capacity: Int(infoCount)) {
            vm_region_64(mach_task_self_, &region, &regionSize, VM_REGION_BASIC_INFO_64, $0, &infoCount, &objectName)
        }
    }
    
    if result != KERN_SUCCESS {
        // print("Failed to reach \(address)")
        return false
    }
    
    return info.protection & VM_PROT_EXECUTE != 0
}
func allocateTest() -> Bool {
    let pageSize = sysconf(_SC_PAGESIZE)
    let code: [UInt32] = [0x52800540, 0xD65F03C0]
    
    guard let jitMemory = mmap(nil, pageSize, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0), jitMemory != MAP_FAILED else {
        return false
    }
    
    defer {
        munmap(jitMemory, pageSize)
    }
    
    
    memcpy(jitMemory, code, code.count)
    
    _ = mprotect(jitMemory, pageSize, PROT_READ | PROT_EXEC)
    
    let checkMem = checkMemoryPermissions(at: jitMemory)
    
    return checkMem
}

// thank you nikki (nythepegasus)
extension FileManager {
    func filePath(atPath path: String, withLength length: Int) -> String? {
        guard let file = try? contentsOfDirectory(atPath: path).filter({ $0.count == length }).first else { return nil }
        return "\(path)/\(file)"
    }
}

/// Reads the IODeviceTree memory-map keys and looks for the "TXM" entry.
/// This is the check MeloNX 2.7 / StikDebug perform via IOKit; Meliora has no
/// StikJITIOKit dependency, so the C functions are resolved with dlsym.
/// Returns nil when inconclusive (IOKit unavailable or property unreadable).
private func isTXMIOKit() -> Bool? {
    guard let handle = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_LAZY) else {
        return nil
    }
    defer { dlclose(handle) }

    typealias IORegistryEntryFromPathFn = @convention(c) (UInt32, UnsafePointer<CChar>) -> UInt32
    typealias IORegistryEntryCreateCFPropertyFn = @convention(c) (UInt32, CFString, CFAllocator?, UInt32) -> Unmanaged<CFTypeRef>?
    typealias IOObjectReleaseFn = @convention(c) (UInt32) -> Int32

    guard
        let fromPathSym = dlsym(handle, "IORegistryEntryFromPath"),
        let createPropertySym = dlsym(handle, "IORegistryEntryCreateCFProperty"),
        let releaseSym = dlsym(handle, "IOObjectRelease")
    else {
        return nil
    }

    let fromPath = unsafeBitCast(fromPathSym, to: IORegistryEntryFromPathFn.self)
    let createProperty = unsafeBitCast(createPropertySym, to: IORegistryEntryCreateCFPropertyFn.self)
    let objectRelease = unsafeBitCast(releaseSym, to: IOObjectReleaseFn.self)

    // kIOMainPortDefault is 0.
    let entry = fromPath(0, "IODeviceTree:/chosen/memory-map")
    guard entry != 0 else { return nil }

    let keysCF = createProperty(entry, "IORegistryEntryPropertyKeys" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue()
    _ = objectRelease(entry)
    guard let keys = keysCF else { return nil }

    if let list = keys as? [String] {
        return list.contains("TXM")
    }
    if let data = keys as? Data,
       let list = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String] {
        return list.contains("TXM")
    }
    return nil
}

func notnil(_ condition: Any?) -> Bool {
    if let _ = condition {
        return false
    } else {
        return true
    }
}

public extension ProcessInfo {
    var hasTXM: Bool {
        if isiOSAppOnMac {
            return false
        }

        // 1. IOKit device-tree check (what MeloNX 2.7 / StikDebug use).
        if let txm = isTXMIOKit() {
            return txm
        }

        // 2. Classic preboot directory check.
        if let boot = FileManager.default.filePath(atPath: "/System/Volumes/Preboot", withLength: 36),
           let file = FileManager.default.filePath(atPath: "\(boot)/boot", withLength: 96),
           access("\(file)/usr/standalone/firmware/FUD/Ap,TrustedExecutionMonitor.img4", F_OK) == 0 {
            return true
        }
        if let file = FileManager.default.filePath(atPath: "/private/preboot", withLength: 96),
           access("\(file)/usr/standalone/firmware/FUD/Ap,TrustedExecutionMonitor.img4", F_OK) == 0 {
            return true
        }

        // 3. Both checks inconclusive (iOS 27.x sandbox blocks the Preboot
        //    directory listings): every device still receiving iOS 26+
        //    enforces TXM, so assume present instead of misdetecting TXM
        //    devices as non-TXM and taking a JIT path that cannot work.
        if operatingSystemVersion.majorVersion >= 26 {
            return true
        }

        return false
    }
}
