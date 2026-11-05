// The client's neighbours that ALVRClient/EventHandler.swift, FFR.swift and Settings.swift touch, as the Swift headset needs them.
// Unlike tools/protocol-harness (scripted streamer, fake NALs), client_core here is the REAL one (the fork's ALVR submodule built
// for Linux, harness/swift_headset/build.sh) connected to the real streamer, so the decoder stand-in receives real HEVC / AV1
// access units: it writes them to the stream dump the harness decodes with ffmpeg and answers every frame as decoded.
// Each stand-in names the client lines it mirrors.

import Foundation
import AppleShims

// MARK: - Globals the client reads

let renderWidth = Int(1888)          // MetalClientSystem.swift:26
let renderHeight = Int(1824)         // MetalClientSystem.swift:27
let defaultRefreshRate = Float(90.0) // VideoHandler.swift:17
var refreshRate = defaultRefreshRate // VideoHandler.swift:18

/// Shaders.metal function constant indices (FFR.makeFunctionConstants only needs distinct values here)
enum ALVRFunctionConstant: Int {
    case ffrEnabled = 0, ffrCommonShaderEyeSizeRatio, ffrCommonShaderCenterSize, ffrCommonShaderCenterShift, ffrCommonShaderEdgeRatio
}

// MARK: - print / exit: the client's own lines go to the client log, not to stdout (stdout carries the harness's JSON lines)

func print(_ items: Any..., separator: String = " ", terminator: String = "\n") {
    HeadsetLog.shared.client(items.map { "\($0)" }.joined(separator: separator))
}

func exit(_ code: Int32) -> Never {
    HeadsetLog.shared.event("client_exit", ["code": code])
    Headset.shared.finish(reason: "client called exit(\(code))")
}

final class HeadsetLog: @unchecked Sendable {
    static let shared = HeadsetLog()
    let origin = CACurrentMediaTime()
    private let lock = NSLock()
    private var file: FileHandle? = nil
    func open(_ path: String) {
        FileManager.default.createFile(atPath: path, contents: nil)
        file = FileHandle(forWritingAtPath: path)
    }
    func client(_ line: String) {
        lock.lock(); defer { lock.unlock() }
        file?.write(String(format: "%9.3f ", CACurrentMediaTime() - origin).data(using: .utf8)! + (line + "\n").data(using: .utf8)!)
    }
    /// One JSON line on stdout, the Rust mock's format ({"t_ms", "event", "data"}).
    func event(_ name: String, _ data: Any? = nil) {
        let obj: [String: Any] = ["t_ms": (CACurrentMediaTime() - origin) * 1000, "event": name, "data": data ?? NSNull()]
        if let d = try? JSONSerialization.data(withJSONObject: obj), let s = String(data: d, encoding: .utf8) {
            lock.lock(); Swift.print(s); fflush(stdout); lock.unlock()
        }
    }
}

// MARK: - WorldTracker (tracking uplink only)

final class WorldTracker: @unchecked Sendable {
    nonisolated(unsafe) static let shared = WorldTracker()
    static let deviceIdHead: UInt64 = alvr_path_string_to_id("/user/head")
    static let deviceIdLeftHand: UInt64 = alvr_path_string_to_id("/user/hand/left")
    static let deviceIdRightHand: UInt64 = alvr_path_string_to_id("/user/hand/right")

    var leftControllerBatteryPercent: Float = 1.0
    var leftControllerBatteryIsCharging = false
    var rightControllerBatteryPercent: Float = 1.0
    var rightControllerBatteryIsCharging = false
    var leftHapticsStart = 0.0, leftHapticsEnd = 0.0
    var leftHapticsFreq: Float = 0, leftHapticsAmplitude: Float = 0
    var rightHapticsStart = 0.0, rightHapticsEnd = 0.0
    var rightHapticsFreq: Float = 0, rightHapticsAmplitude: Float = 0
    var needsRecenterTrigger = false

    func initializeAr() async {}

    /// WorldTracker.swift sendFakeTracking: dummy view params + one head motion at (0, 1.2, 0), identity orientation
    func sendFakeTracking(viewFovs: [AlvrFov], targetTimestamp: Double) {
        if viewFovs.isEmpty { return }
        let targetTimestampNS = UInt64(max(0, targetTimestamp) * Double(NSEC_PER_SEC))
        let p = UnsafeMutablePointer<AlvrViewParams>.allocate(capacity: 2)
        defer { p.deallocate() }
        p[0] = AlvrViewParams(pose: AlvrPose(orientation: AlvrQuat(x: 0, y: 0, z: 0, w: 1), position: (0, 0, 0)), fov: viewFovs[0])
        p[1] = AlvrViewParams(pose: AlvrPose(orientation: AlvrQuat(x: 0, y: 0, z: 0, w: 1), position: (0, 0, 0)), fov: viewFovs[1])
        var motion = AlvrDeviceMotion()
        motion.device_id = WorldTracker.deviceIdHead
        motion.pose = AlvrPose(orientation: AlvrQuat(x: 0, y: 0, z: 0, w: 1), position: (0.0, 1.2, 0.0))
        alvr_send_view_params(UnsafePointer(p))
        var motions = [motion]
        alvr_send_tracking(targetTimestampNS, &motions, 1, nil, nil)
        print("Sending fake tracking...")
    }

    /// WorldTracker.swift sendViewParams (same body; the client's simd_quaternion of an identity orientation is the identity)
    func sendViewParams(viewTransforms: [simd_float4x4], viewFovs: [AlvrFov]) {
        let p = UnsafeMutablePointer<AlvrViewParams>.allocate(capacity: 2)
        defer { p.deallocate() }
        let l = viewTransforms[0].columns.3, r = viewTransforms[1].columns.3
        p[0] = AlvrViewParams(pose: AlvrPose(orientation: AlvrQuat(x: 0, y: 0, z: 0, w: 1), position: (l.x, l.y, l.z)), fov: viewFovs[0])
        p[1] = AlvrViewParams(pose: AlvrPose(orientation: AlvrQuat(x: 0, y: 0, z: 0, w: 1), position: (r.x, r.y, r.z)), fov: viewFovs[1])
        alvr_send_view_params(UnsafePointer(p))
        Headset.shared.viewsDeclared(viewFovs)
    }

    /// WorldTracker.swift sendTracking reduced to the head: the pose for this presentation, on the outgoing worker like the client.
    func sendTracking(reportedTargetTimestamp: Double) {
        let ns = UInt64(reportedTargetTimestamp * Double(NSEC_PER_SEC))
        EventHandler.shared.lastRequestedTimestamp = ns
        let (yaw, q) = Headset.shared.headPose(at: reportedTargetTimestamp)
        Headset.shared.recordSentPose(ns: ns, yaw: yaw, q: q)
        EventHandler.shared.outgoingWorker.enqueue {
            var motion = AlvrDeviceMotion()
            motion.device_id = WorldTracker.deviceIdHead
            motion.pose = AlvrPose(orientation: AlvrQuat(x: q.x, y: q.y, z: q.z, w: q.w), position: (0, Headset.shared.height, 0))
            var motions = [motion]
            alvr_send_tracking_and_face_data(ns, &motions, 1, nil, nil, nil)
        }
    }
}

// MARK: - Inert neighbours

final class SteamControllerManager {
    enum Owner { case streaming }
    nonisolated(unsafe) static let shared = SteamControllerManager()
    func stopAll() {}
    func stop(_ o: Owner) {}
    func start(_ o: Owner) {}
}

final class InputDebugSession {
    nonisolated(unsafe) static let shared = InputDebugSession()
    func suspend() {}
    func resume() {}
}

struct HeadsetSettings { var showPerformanceHud = false }
final class HeadsetStore { var settings = HeadsetSettings() }
enum ALVRClientApp { nonisolated(unsafe) static let gStore = HeadsetStore() }

final class PerformanceTracker {
    nonisolated(unsafe) static let shared = PerformanceTracker()
    func recordReceive(timestampNs: UInt64) {}
    func recordDecodeEnd(timestampNs: UInt64) {}
}

// MARK: - VideoHandler: no VideoToolbox; the access units go to the stream dump, every frame counts as decoded

struct VideoHandler {
    /// VideoHandler.swift createVideoDecoder: a session per decoder config (the parameter sets, kept for the dump's head)
    static func createVideoDecoder(initialNals: UnsafeMutableBufferPointer<UInt8>, codec: Int) -> (VTDecompressionSession?, CMFormatDescription?) {
        let bytes = Array(initialNals)
        Headset.shared.decoderConfig(bytes, codec: codec)
        let fmt = CMFormatDescription(width: 0, height: 0, configId: UInt32(truncatingIfNeeded: bytes.count))
        return (VTDecompressionSession(format: fmt), fmt)
    }

    /// VideoHandler.swift feedVideoIntoDecoder: VideoToolbox decodes synchronously (flags 0); here the frame is written to the dump
    /// and handed back as a picture at once (decode time: --decode-ms).
    static func feedVideoIntoDecoder(decompressionSession: VTDecompressionSession, nals: UnsafeMutableBufferPointer<UInt8>, timestamp: UInt64, videoFormat: CMFormatDescription, codec: Int, callback: @escaping (_ imageBuffer: CVImageBuffer?) -> Void) {
        let index = Headset.shared.frameArrived(nals, timestamp: timestamp)
        if Headset.shared.decodeMs > 0 { usleep(UInt32(Headset.shared.decodeMs * 1000)) }
        decompressionSession.decodedCount += 1
        callback(CVImageBuffer(frameIndex: index, timestamp: timestamp))
    }
}
