// The Swift headset: a Vision Pro stand-in for the lab that runs the visionOS client's own code against the real streamer.
//
// Real (compiled unchanged from the client fork at a git ref, build.sh): ALVRClient/EventHandler.swift (event thread, NAL
// callback, frame queue, decoder lifecycle), FFR.swift (the client's foveation variables), Settings.swift (the streamer's settings
// JSON), Extensions/Drawable.swift (frustum tangents from the projection, the canted-view conversion) and client_core itself (the
// fork's ALVR submodule, Rust, built for this OS). Mirrored line by line: Renderer.renderFrame's protocol part (frame pick, the
// view declaration of Renderer.swift:830-890 on airframexr-client, tracking per presentation, submit report). Absent: Metal,
// VideoToolbox (the access units go to a dump the harness decodes with ffmpeg), ARKit (the head follows the Rust mock's motion).
//
// Command line: the Rust mock's flags where they mean the same (harness/lib.py CLIENT_FLAGS), so harness/run.py runs either:
//   --report F --frames-csv F --poses-csv F --out F.hevc --seconds S --connect-timeout S --exit-after-frames N --min-frames N
//   --yaw-amp R --yaw-hz H --height M --decode-ms MS --display-hz HZ
//   --views-tan o,i,u,d   Apple's frustum for the LEFT eye (outer, inner, up, down tangents; the right eye mirrored): what
//                         computeProjection would return; the client's own code decides what is declared. Default 1.732,1,1.192,1.
//   --ipd-mm MM           default 63 (the client reads the IPD from the views' positions)
//   --projection-y-flipped  build the projection with the other vertical convention (see HeadsetShims.swift)
//   --log F               the client's own print lines (default next to the report)
// Other flags of the Rust mock are accepted and reported as ignored.

import Foundation
import AppleShims

final class Headset: @unchecked Sendable {
    static let shared = Headset()

    // options
    var reportPath = "client_report.json", framesCsv: String? = nil, posesCsv: String? = nil, dumpPath: String? = nil
    var seconds = 30.0, connectTimeout = 30.0, exitAfterFrames: Int? = nil, minFrames = 0
    var yawAmp: Float = 0.0, yawHz: Float = 0.25, height: Float = 1.6, decodeMs = 0.0, displayHz = 90.0
    var frustum: (o: Float, i: Float, u: Float, d: Float) = (1.732, 1.0, 1.192, 1.0)
    var ipd: Float = 0.063, yFlipped = false
    var ignoredFlags: [String] = []

    // state
    let lock = NSLock()
    let origin = CACurrentMediaTime()
    var connectedAt: Double? = nil, streamStartedAt: Double? = nil
    var codec = -1
    var configBytes = 0
    var frames: [(ts: UInt64, arrival: UInt64, len: Int, yaw: Float?, yawNear: Float?)] = []
    var bytes = 0
    var sentYaw: [UInt64: Float] = [:]
    var sentPoses: [(ts: UInt64, yaw: Float, send: UInt64, q: SIMD4<Float>, wall: UInt64)] = []
    var declared: [[Float]] = []          // per eye, outer / inner / up / down tangents, as sent (from the AlvrFov angles)
    var declaredAtMs: Double? = nil
    var shownNew = 0, shownRepeated = 0, shownBlank = 0, ticks = 0
    var dump: FileHandle? = nil
    var drawable: LayerRenderer.Drawable! = nil
    var finished = false

    func nowNs() -> UInt64 { UInt64(CACurrentMediaTime() * Double(NSEC_PER_SEC)) }

    // MARK: head motion (the Rust mock's default profile: yaw = amp sin(2 pi hz t), t from the start)
    func headPose(at t: Double) -> (Float, SIMD4<Float>) {
        let yaw = yawAmp * sinf(2 * Float.pi * yawHz * Float(t - origin))
        return (yaw, SIMD4(0, sinf(yaw / 2), 0, cosf(yaw / 2)))
    }
    func recordSentPose(ns: UInt64, yaw: Float, q: SIMD4<Float>) {
        lock.lock(); defer { lock.unlock() }
        sentYaw[ns] = yaw
        sentPoses.append((ns, yaw, nowNs(), q, UInt64(Date().timeIntervalSince1970 * 1e9)))
    }

    // MARK: called from the client's code paths (Neighbours.swift)
    func decoderConfig(_ b: [UInt8], codec c: Int) {
        lock.lock(); defer { lock.unlock() }
        codec = c
        configBytes = b.count
        HeadsetLog.shared.event("decoder_config", ["codec": c, "config_nal_bytes": b.count])
        dump?.write(Data(b))
    }
    func frameArrived(_ nals: UnsafeMutableBufferPointer<UInt8>, timestamp: UInt64) -> UInt32 {
        lock.lock(); defer { lock.unlock() }
        let yaw = sentYaw[timestamp]
        let near = sentYaw.first { abs(Int64(bitPattern: $0.key &- timestamp)) <= 3000 }?.value
        frames.append((timestamp, nowNs(), nals.count, yaw, near))
        bytes += nals.count
        if let base = nals.baseAddress { dump?.write(Data(bytes: base, count: nals.count)) }
        return UInt32(truncatingIfNeeded: frames.count)
    }
    func viewsDeclared(_ fovs: [AlvrFov]) {
        lock.lock(); defer { lock.unlock() }
        // left eye: left = outer; right eye: right = outer
        declared = fovs.enumerated().map { i, f in
            let l = tanf(-f.left), r = tanf(f.right), u = tanf(f.up), d = tanf(-f.down)
            return (i == 0 ? [l, r, u, d] : [r, l, u, d]).map { ($0 * 10000).rounded() / 10000 }
        }
        declaredAtMs = (CACurrentMediaTime() - origin) * 1000
        HeadsetLog.shared.event("views_declared", ["left_oiud": declared[0], "right_oiud": declared.count > 1 ? declared[1] : []])
    }

    // MARK: the render thread (Renderer.renderFrame, protocol part)
    func renderLoop() {
        let eh = EventHandler.shared
        eh.handleRenderStarted()          // Renderer.swift: render started (immersive space open before the stream, as in the field)
        eh.renderStarted = true
        eh.handleHeadsetRemovedOrReentry()
        let period = 1.0 / displayHz
        var nextVsync = CACurrentMediaTime() + period
        while !finished && eh.renderStarted {
            let optimalInput = nextVsync - period
            let now = CACurrentMediaTime()
            if optimalInput > now { usleep(UInt32((optimalInput - now) * 1e6)) }
            renderFrame(vsyncTime: nextVsync)
            nextVsync += period
            let late = CACurrentMediaTime() - nextVsync
            if late > 0 { nextVsync += (late / period).rounded(.up) * period }
        }
    }

    private func renderFrame(vsyncTime: Double) {
        let eh = EventHandler.shared
        eh.framesRendered += 1
        eh.totalFramesRendered += 1
        var streamingActiveForFrame = eh.streamingActive
        var isReprojected = false
        var queuedFrame: QueuedFrame? = nil
        switch eh.pollFrameForDisplay(timeout: 0.005) {
        case .resubmit(let f): queuedFrame = f; eh.framesRendered -= 1
        case .fresh(let f): queuedFrame = f
        case .reprojected(let f): queuedFrame = f; eh.framesRendered -= 1; isReprojected = true
        case .none: break
        }
        if queuedFrame == nil && streamingActiveForFrame { streamingActiveForFrame = false }
        let renderingStreaming = streamingActiveForFrame && queuedFrame != nil

        if queuedFrame != nil && !queuedFrame!.viewParamsValid {
            let p = UnsafeMutablePointer<AlvrViewParams>.allocate(capacity: 2)
            defer { p.deallocate() }
            alvr_report_compositor_start(queuedFrame!.timestamp, p)
            queuedFrame = QueuedFrame(imageBuffer: queuedFrame!.imageBuffer, timestamp: queuedFrame!.timestamp, viewParamsValid: true, viewParams: [p[0], p[1]])
        }

        // Renderer.swift (airframexr-client) "Send view config": the real Drawable.swift functions on a drawable built from the frustum
        let mainDrawable = drawable!
        if eh.alvrInitialized && streamingActiveForFrame {
            let ipd = mainDrawable.views.count > 1 ? simd_length(mainDrawable.views[0].transform.columns.3 - mainDrawable.views[1].transform.columns.3) : 0.063
            if abs(eh.lastIpd - ipd) > 0.001 {
                print("Send view config")
                if eh.lastIpd == -1 { print("IPD is", ipd); eh.framesRendered = 0 }
                let v = Headset.declaration(mainDrawable)
                eh.viewFovs = v.fovs
                eh.viewTransforms = v.transforms
                eh.lastIpd = ipd
                eh.sentViewTangents = v.sentTangents
                eh.realViewTangents = v.realTangents
                WorldTracker.shared.sendViewParams(viewTransforms: eh.viewTransforms, viewFovs: eh.viewFovs)
            }
        }

        objc_sync_enter(eh.frameQueueLock)
        eh.framesSinceLastDecode += 1
        objc_sync_exit(eh.frameQueueLock)

        // tracking for this presentation, once the views are declared (Renderer.swift: lastIpd != -1)
        if eh.alvrInitialized && eh.lastIpd != -1 {
            WorldTracker.shared.sendTracking(reportedTargetTimestamp: vsyncTime)
        }

        // the command buffer's completion (~ a few ms of GPU work later): report the submit once per frame
        let vsyncTimeNs = UInt64(vsyncTime * Double(NSEC_PER_SEC))
        let submitted = queuedFrame
        DispatchQueue.global(qos: .userInteractive).asyncAfter(deadline: .now() + 0.004) {
            if eh.alvrInitialized, let submitted, eh.markSubmitted(submitted.timestamp) {
                let currentTimeNs = UInt64(CACurrentMediaTime() * Double(NSEC_PER_SEC))
                alvr_report_submit(submitted.timestamp, vsyncTimeNs > currentTimeNs ? vsyncTimeNs - currentTimeNs : 0)
            }
        }

        var suitable = true
        if eh.lastIpd == -1 || eh.framesRendered < Int(refreshRate) { suitable = false }
        if eh.videoFormat == nil { suitable = false }
        lock.lock()
        ticks += 1
        if renderingStreaming && suitable && queuedFrame != nil { if isReprojected { shownRepeated += 1 } else { shownNew += 1 } } else { shownBlank += 1 }
        lock.unlock()
        eh.publishShownFrame(queuedFrame)
    }

    /// Renderer.swift (airframexr-client) "Send view config", the computation only: the client's real gimmeTangents and
    /// _cantedViewToProportionalCircumscribedOrthogonal (Extensions/Drawable.swift) and its AlvrFov construction, line for line.
    static func declaration(_ mainDrawable: LayerRenderer.Drawable) -> (fovs: [AlvrFov], transforms: [simd_float4x4], sentTangents: [simd_float4], realTangents: [simd_float4]) {
        let tangentsLeft = mainDrawable.gimmeTangents(viewIndex: 0)
        let tangentsRight = mainDrawable.gimmeTangents(viewIndex: 1)
        let transformLeft = mainDrawable.views[0].transform
        let transformRight = mainDrawable.views[1].transform
        let leftAngles = atan(tangentsLeft)
        let rightAngles = mainDrawable.views.count > 1 ? atan(tangentsRight) : leftAngles
        let leftFov = AlvrFov(left: -leftAngles.x, right: leftAngles.y, up: leftAngles.z, down: -leftAngles.w)
        let rightFov = AlvrFov(left: -rightAngles.x, right: rightAngles.y, up: rightAngles.z, down: -rightAngles.w)
        let (leftFovCorrected, tangentsLeftCorrected, transformLeftCorrected) = mainDrawable._cantedViewToProportionalCircumscribedOrthogonal(fov: leftFov, viewTransform: transformLeft, fovPostScale: 1.0)
        let (rightFovCorrected, tangentsRightCorrected, transformRightCorrected) = mainDrawable._cantedViewToProportionalCircumscribedOrthogonal(fov: rightFov, viewTransform: transformRight, fovPostScale: 1.0)
        return ([leftFovCorrected, rightFovCorrected], [transformLeftCorrected.translationOnly(), transformRightCorrected.translationOnly()],   // Renderer.fixTransform
                [tangentsLeftCorrected, tangentsRightCorrected], [tangentsLeft, tangentsRight])
    }

    /// --declare-selftest: what the client declares for a known frustum, under both vertical conventions of the projection matrix.
    /// Prints one JSON line per convention; exit 0 when the declared box equals the frustum (outer, inner, up, down) under the standard one.
    static func declareSelftest(_ f: (o: Float, i: Float, u: Float, d: Float)) -> Never {
        var ok = true
        for flipped in [false, true] {
            let dr = LayerRenderer.Drawable(tan: [(f.o, f.i, f.u, f.d), (f.i, f.o, f.u, f.d)], ipd: 0.063, projectionYFlipped: flipped)
            let v = declaration(dr)
            let l = v.fovs[0]
            let got = [tanf(-l.left), tanf(l.right), tanf(l.up), tanf(-l.down)].map { ($0 * 1000).rounded() / 1000 }
            let want = [f.o, f.i, f.u, f.d].map { ($0 * 1000).rounded() / 1000 }
            let match = got == want
            if !flipped { ok = match }
            let obj: [String: Any] = ["projection": flipped ? "y flipped" : "standard (OpenGL / Metal clip space, y up)", "frustum_left_oiud": want,
                                      "declared_left_oiud": got, "declared_equals_frustum": match, "up_down_swapped": got[2] == want[3] && got[3] == want[2] && want[2] != want[3]]
            if let d = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]), let s = String(data: d, encoding: .utf8) { Swift.print(s) }
        }
        Glibc.exit(ok ? 0 : 1)
    }

    // MARK: the run
    func parse(_ a: [String]) {
        var i = 1
        func next() -> String { i += 1; return i < a.count ? a[i] : "" }
        while i < a.count {
            let k = a[i]
            switch k {
            case "--report": reportPath = next()
            case "--frames-csv": framesCsv = next()
            case "--poses-csv": posesCsv = next()
            case "--out": dumpPath = next()
            case "--log": HeadsetLog.shared.open(next())
            case "--seconds": seconds = Double(next()) ?? seconds
            case "--connect-timeout": connectTimeout = Double(next()) ?? connectTimeout
            case "--exit-after-frames": exitAfterFrames = Int(next())
            case "--min-frames": minFrames = Int(next()) ?? 0
            case "--yaw-amp": yawAmp = Float(next()) ?? 0
            case "--yaw-hz": yawHz = Float(next()) ?? yawHz
            case "--height": height = Float(next()) ?? height
            case "--decode-ms": decodeMs = Double(next()) ?? 0
            case "--display-hz", "--refresh": displayHz = Double(next()) ?? displayHz
            case "--ipd-mm": ipd = (Float(next()) ?? 63) / 1000
            case "--projection-y-flipped": yFlipped = true
            case "--declare-selftest": break
            case "--views-tan", "--frustum":
                let p = next().split(separator: ",").compactMap { Float($0) }
                if p.count == 4 { frustum = (p[0], p[1], p[2], p[3]) }
            default:
                ignoredFlags.append(k)
                if i + 1 < a.count, !a[i + 1].hasPrefix("--") { i += 1 }
            }
            i += 1
        }
    }

    func run() {
        parse(CommandLine.arguments)
        if CommandLine.arguments.contains("--declare-selftest") { Headset.declareSelftest(frustum) }
        if !FileManager.default.fileExists(atPath: reportPath + ".log") { HeadsetLog.shared.open(reportPath + ".log") }
        if let p = dumpPath { FileManager.default.createFile(atPath: p, contents: nil); dump = FileHandle(forWritingAtPath: p) }
        // left eye: left = outer; right eye mirrored
        drawable = LayerRenderer.Drawable(tan: [(frustum.o, frustum.i, frustum.u, frustum.d), (frustum.i, frustum.o, frustum.u, frustum.d)], ipd: ipd, projectionYFlipped: yFlipped)
        HeadsetLog.shared.event("start", ["frustum_left_oiud": [frustum.o, frustum.i, frustum.u, frustum.d], "ipd_m": ipd, "projection_y_flipped": yFlipped, "ignored_flags": ignoredFlags])

        // ALVRClientApp.swift (scene active): initialise the core, start the event thread
        EventHandler.shared.initializeAlvr()
        EventHandler.shared.start()
        let render = Thread { self.renderLoop() }
        render.name = "Render Thread"
        render.start()

        Thread {
            while true {
                usleep(20_000)
                let t = CACurrentMediaTime() - self.origin
                let eh = EventHandler.shared
                if eh.streamingActive && self.streamStartedAt == nil {
                    self.streamStartedAt = CACurrentMediaTime()
                    self.connectedAt = t
                    HeadsetLog.shared.event("streaming_started", self.negotiated())
                }
                if let s = self.streamStartedAt {
                    if CACurrentMediaTime() - s > self.seconds { self.finish(reason: "seconds"); }
                    self.lock.lock(); let n = self.frames.count; self.lock.unlock()
                    if let e = self.exitAfterFrames, n >= e { self.finish(reason: "exit_after_frames") }
                } else if t > self.connectTimeout {
                    self.finish(reason: "connect_timeout")
                }
            }
        }.start()
        dispatchMain()
    }

    func negotiated() -> [String: Any] {
        guard let ev = EventHandler.shared.streamEvent else { return [:] }
        let s = ev.STREAMING_STARTED
        return ["view_resolution": [s.view_width, s.view_height], "enable_foveated_encoding": s.enable_foveated_encoding,
                "refresh_rate_hint": s.refresh_rate_hint, "encoding_gamma": s.encoding_gamma, "enable_hdr": s.enable_hdr]
    }

    /// The client's own foveation variables (FFR.calculateFoveationVars from the streamer's settings, as Renderer.rebuildRenderPipelines does)
    func clientFoveation() -> [String: Any] {
        guard let ev = EventHandler.shared.streamEvent else { return ["error": "no stream"] }
        Settings.clearSettingsCache()
        guard let settings = Settings.getAlvrSettings() else { return ["error": "settings JSON not decoded"] }
        let v = FFR.calculateFoveationVars(alvrEvent: ev.STREAMING_STARTED, foveationSettings: settings.video.foveated_encoding)
        return ["enabled": v.enabled, "target_eye": [v.targetEyeWidth, v.targetEyeHeight], "optimized_eye": [v.optimizedEyeWidth, v.optimizedEyeHeight],
                "frame": [2 * v.optimizedEyeWidth, v.optimizedEyeHeight], "eye_size_ratio": [v.eyeWidthRatio, v.eyeHeightRatio],
                "center_size": [v.centerSizeX, v.centerSizeY], "center_shift": [v.centerShiftX, v.centerShiftY], "edge_ratio": [v.edgeRatioX, v.edgeRatioY]]
    }

    func stats(_ v: [Double]) -> [String: Any] {
        let s = v.sorted()
        func pct(_ p: Double) -> Double { s.isEmpty ? 0 : s[min(s.count - 1, Int(Double(s.count - 1) * p))] }
        return ["n": s.count, "p50": pct(0.5), "p95": pct(0.95), "max": s.last ?? 0]
    }

    func finish(reason: String) -> Never {
        lock.lock()
        if finished { lock.unlock(); while true { sleep(1) } }
        finished = true
        let fr = frames, poses = sentPoses
        lock.unlock()
        let fov = clientFoveation()
        let neg = negotiated()
        alvr_pause()
        try? dump?.close()
        let n = fr.count
        let span = n > 1 ? Double(fr[n - 1].arrival - fr[0].arrival) / 1e9 : 0
        let inter = zip(fr, fr.dropFirst()).map { Double($1.arrival - $0.arrival) / 1e6 }
        let lat = fr.map { (Double($0.arrival) - Double($0.ts)) / 1e6 }
        let mono = zip(fr, fr.dropFirst()).allSatisfy { $1.ts >= $0.ts }
        let dup = zip(fr, fr.dropFirst()).filter { $1.ts == $0.ts }.count
        let matched = fr.filter { $0.yawNear != nil }.count, exact = fr.filter { $0.yaw != nil }.count
        let codecName = [0: "H264", 1: "Hevc", 2: "AV1"][codec] ?? "none"
        let connected = streamStartedAt != nil
        let pass = connected && n >= minFrames
        var report: [String: Any] = [
            "pass": pass, "client": "swift", "end_reason": reason, "connected": connected, "connected_at_s": connectedAt ?? NSNull(),
            "codec": codecName, "negotiated_config": neg, "decoder_config_nal_bytes": configBytes, "frames": n, "bytes": bytes, "span_s": span,
            "fps_measured": span > 0 ? Double(n - 1) / span : 0, "mbps_measured": span > 0 ? Double(bytes) * 8 / span / 1e6 : 0,
            "interarrival_ms": stats(inter), "arrival_minus_timestamp_ms": stats(lat), "timestamps_monotonic": mono, "duplicate_timestamps": dup,
            "frames_with_matching_sent_pose": matched, "frames_with_exact_sent_pose": exact, "poses_sent": poses.count,
            "views_sent_at_ms": declaredAtMs ?? NSNull(),
        ]
        report["swift"] = ["frustum_left_oiud": [frustum.o, frustum.i, frustum.u, frustum.d], "projection_y_flipped": yFlipped, "ipd_m": ipd,
                           "declared_oiud": declared, "client_foveation": fov, "ignored_flags": ignoredFlags,
                           "display": ["ticks": ticks, "new": shownNew, "repeated": shownRepeated, "blank": shownBlank]]
        if let d = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) {
            FileManager.default.createFile(atPath: reportPath, contents: d)
        }
        if let p = framesCsv {
            var s = "ts_ns,arrival_ns,len,yaw,yaw_near,decoded_ns,queue_dropped\n"
            for f in fr { s += "\(f.ts),\(f.arrival),\(f.len),\(f.yaw.map { "\($0)" } ?? ""),\(f.yawNear.map { "\($0)" } ?? ""),0,false\n" }
            FileManager.default.createFile(atPath: p, contents: s.data(using: .utf8))
        }
        if let p = posesCsv {
            var s = "target_ts_ns,yaw,send_ns,qx,qy,qz,qw,px,py,pz,wall_ns\n"
            for q in poses { s += "\(q.ts),\(q.yaw),\(q.send),\(q.q.x),\(q.q.y),\(q.q.z),\(q.q.w),0,\(height),0,\(q.wall)\n" }
            FileManager.default.createFile(atPath: p, contents: s.data(using: .utf8))
        }
        HeadsetLog.shared.event("report", report)
        Glibc.exit(pass ? 0 : 1)
    }
}

Headset.shared.run()
