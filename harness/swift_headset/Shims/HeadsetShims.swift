// Compiled into the AppleShims module next to the protocol harness's AppleShims.swift (taken from the client fork at the same git
// ref, see build.sh): the extra Apple surface the Swift headset needs to compile the client's REAL Extensions/Drawable.swift,
// FFR.swift and Settings.swift. Everything here is either plain maths or inert.

@_exported import Foundation   // Apple's frameworks re-export Darwin (tan, atan...) to files that import only them

// MARK: - simd (only what the compiled client files use)

public typealias matrix_float4x4 = simd_float4x4
public typealias simd_float2 = SIMD2<Float>

public struct simd_float3x3 {
    public var columns: (SIMD3<Float>, SIMD3<Float>, SIMD3<Float>)
    public init(_ c0: SIMD3<Float>, _ c1: SIMD3<Float>, _ c2: SIMD3<Float>) { columns = (c0, c1, c2) }
}

public func * (m: simd_float3x3, v: SIMD3<Float>) -> SIMD3<Float> {
    m.columns.0 * v.x + m.columns.1 * v.y + m.columns.2 * v.z
}

public extension simd_float4x4 {
    init() { self.init(SIMD4(0, 0, 0, 0), SIMD4(0, 0, 0, 0), SIMD4(0, 0, 0, 0), SIMD4(0, 0, 0, 0)) }
    /// simd's subscript: [column, row]
    subscript(_ column: Int, _ row: Int) -> Float {
        let c: SIMD4<Float>
        switch column { case 0: c = columns.0; case 1: c = columns.1; case 2: c = columns.2; default: c = columns.3 }
        return c[row]
    }
    /// Extensions/simd.swift:97-99 (the client's own, same body)
    func orientationOnly() -> simd_float3x3 {
        simd_float3x3(SIMD3(columns.0.x, columns.0.y, columns.0.z), SIMD3(columns.1.x, columns.1.y, columns.1.z), SIMD3(columns.2.x, columns.2.y, columns.2.z))
    }
    /// Extensions/simd.swift:101-103
    func translationOnly() -> simd_float4x4 {
        simd_float4x4(simd_float4(1.0, 0.0, 0.0, 0.0), simd_float4(0.0, 1.0, 0.0, 0.0), simd_float4(0.0, 0.0, 1.0, 0.0), columns.3)
    }
}

public func atan(_ v: SIMD4<Float>) -> SIMD4<Float> { SIMD4(atanf(v.x), atanf(v.y), atanf(v.z), atanf(v.w)) }
public func simd_length(_ v: SIMD4<Float>) -> Float { (v * v).sum().squareRoot() }

// MARK: - Metal (FFR.makeFunctionConstants only builds constants: inert)

public enum MTLDataType { case bool, float2 }
public final class MTLFunctionConstantValues {
    public init() {}
    public func setConstantValue(_ value: UnsafeRawPointer, type: MTLDataType, index: Int) {}
}

// MARK: - CompositorServices

/// LayerRenderer.Drawable reduced to what Extensions/Drawable.swift reads: the views (transform, tangents) and computeProjection.
/// The projection is built from the frustum the headset is given (outer, inner, up, down tangents per eye) with the standard
/// off-axis layout (OpenGL / Metal clip space, y up): P[2,0] = (r - l) / (r + l), P[2,1] = (u - d) / (u + d), P[0,0] = 2 / (r + l),
/// P[1,1] = 2 / (u + d) for tangents l, r, u, d > 0. Apple's real matrix layout is the one assumption here; `projectionYFlipped`
/// builds the other vertical convention so both can be run.
public enum LayerRenderer {
    public final class Drawable {
        public struct View {
            public var transform: simd_float4x4
            public var tangents: simd_float4
        }
        public var views: [View]
        public let frustumTan: [(l: Float, r: Float, u: Float, d: Float)]
        public let projectionYFlipped: Bool

        public init(tan: [(l: Float, r: Float, u: Float, d: Float)], ipd: Float, projectionYFlipped: Bool = false) {
            self.frustumTan = tan
            self.projectionYFlipped = projectionYFlipped
            views = frustumTan.enumerated().map { i, t in
                var m = matrix_identity_float4x4
                m.columns.3 = SIMD4((i == 0 ? -0.5 : 0.5) * ipd, 0, 0, 1)
                return View(transform: m, tangents: SIMD4(t.l, t.r, t.u, t.d))
            }
        }

        public func computeProjection(viewIndex: Int) -> simd_float4x4 {
            let t = frustumTan[viewIndex]
            let (u, d) = projectionYFlipped ? (t.d, t.u) : (t.u, t.d)
            let near: Float = 0.1
            return simd_float4x4(SIMD4(2 / (t.r + t.l), 0, 0, 0),
                                 SIMD4(0, 2 / (u + d), 0, 0),
                                 SIMD4((t.r - t.l) / (t.r + t.l), (u - d) / (u + d), 0, -1),
                                 SIMD4(0, 0, near, 0))
        }
    }
}
