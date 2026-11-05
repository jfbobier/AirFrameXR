//! The FOV-restriction intake (docs/PICTURE_EDGES_DESIGN.md section 7): from what a game submitted against what it was told, the verdict, and
//! whether the next start of that executable is restricted. Pure logic (std only): main.rs reads the shim's record (ipc.h ShimToHost::gameSubmit*),
//! keeps the per-executable store (config\fov_verdicts.json) and publishes the list of executables told the whole view (HostToShim::unrestrictedExes).
//!
//! Tests: `cargo test -p alvr_host --bins` on the builder, or without the workspace: `rustc --edition 2021 --test tools/host/src/fov_intake.rs`.

/// Tangents per eye, L R U D (the OVR convention: positive; the left eye's L is its outer edge, the right eye's L its inner one).
pub type Box2 = [[f32; 4]; 2];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Verdict {
    /// FOV and size as told (or a later start showed the size following the box): the restriction saves GPU work
    Honours,
    /// any submitted edge differs from the told one (the union of both eyes, a symmetric box, its own frustum)
    OwnFrustum,
    /// FOV as told, the render size's aspect differs from the told size's (a fixed, often square or 16:9 render)
    FixedSize,
    /// FOV as told, aspect as told, size not as told: a resolution scale or a fixed size that keeps the aspect; decided at the next start
    Undecided,
}

impl Verdict {
    pub fn name(&self) -> &'static str {
        match self {
            Verdict::Honours => "honours",
            Verdict::OwnFrustum => "own_frustum",
            Verdict::FixedSize => "fixed_size",
            Verdict::Undecided => "undecided",
        }
    }
    pub fn parse(s: &str) -> Option<Verdict> {
        match s {
            "honours" => Some(Verdict::Honours),
            "own_frustum" => Some(Verdict::OwnFrustum),
            "fixed_size" => Some(Verdict::FixedSize),
            "undecided" => Some(Verdict::Undecided),
            _ => None,
        }
    }
}

/// One start of a game: what the shim told it and what it submitted (the shim's record, ~2 s into the session).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Observation {
    pub told: Box2,
    pub told_size: (i32, i32),
    pub submitted: Box2,
    pub submitted_size: [(i32, i32); 2],
    /// the game was told the whole view at this start (on the list)
    pub unrestricted: bool,
}

/// Submitted edges within this of the told ones count as the same (tangent units; the shim's float round trip is far below it)
pub const FOV_TOL: f32 = 0.01;
/// A render size within this many pixels per axis of the told one is the told one (the app's own even / 32 rounding)
pub const SIZE_TOL_PX: i32 = 2;
/// The aspect of the submitted size against the told size's beyond which it is a fixed size
pub const ASPECT_TOL: f64 = 0.05;

fn same_size(a: (i32, i32), b: (i32, i32), tol: i32) -> bool {
    (a.0 - b.0).abs() <= tol && (a.1 - b.1).abs() <= tol
}

/// The verdict of one start, on its own.
pub fn judge(o: &Observation) -> Verdict {
    for e in 0..2 {
        for k in 0..4 {
            if (o.submitted[e][k] - o.told[e][k]).abs() > FOV_TOL {
                return Verdict::OwnFrustum;
            }
        }
    }
    let (tw, th) = o.told_size;
    if tw <= 0 || th <= 0 {
        return Verdict::Undecided;
    }
    let mut all_told = true;
    for e in 0..2 {
        let (w, h) = o.submitted_size[e];
        if w <= 0 || h <= 0 {
            return Verdict::Undecided;
        }
        let aspect = (w as f64 / h as f64) / (tw as f64 / th as f64);
        if (aspect - 1.0).abs() > ASPECT_TOL {
            return Verdict::FixedSize;
        }
        all_told &= same_size((w, h), (tw, th), SIZE_TOL_PX);
    }
    if all_told { Verdict::Honours } else { Verdict::Undecided }
}

/// Two starts of the same game under two different told sizes: did its render size follow the box? `None` when the told size did not change
/// (nothing learned) or the answer is neither (the size moved, but not with the box).
pub fn resolve(prev: &Observation, now: &Observation) -> Option<Verdict> {
    if same_size(prev.told_size, now.told_size, SIZE_TOL_PX) {
        return None;
    }
    let (pw, ph) = prev.submitted_size[0];
    let (nw, nh) = now.submitted_size[0];
    if pw <= 0 || ph <= 0 || prev.told_size.0 <= 0 || prev.told_size.1 <= 0 {
        return None;
    }
    if same_size((pw, ph), (nw, nh), SIZE_TOL_PX) {
        return Some(Verdict::FixedSize);
    }
    // followed: the rendered size scaled by the ratio of the told sizes, within the rounding (2 px + 1 % per axis)
    let rx = now.told_size.0 as f64 / prev.told_size.0 as f64;
    let ry = now.told_size.1 as f64 / prev.told_size.1 as f64;
    let ex = (pw as f64 * rx - nw as f64).abs();
    let ey = (ph as f64 * ry - nh as f64).abs();
    if ex <= SIZE_TOL_PX as f64 + 0.01 * nw as f64 && ey <= SIZE_TOL_PX as f64 + 0.01 * nh as f64 {
        return Some(Verdict::Honours);
    }
    None
}

/// What the store keeps per executable.
#[derive(Clone, Debug, PartialEq)]
pub struct Record {
    pub verdict: Verdict,
    /// the start the verdict was decided on (and the one before it for `resolve`)
    pub last: Observation,
    /// how it was decided: "one start" or "second start"
    pub decided_by: &'static str,
    /// the user's per-game tick: Some(true) always restrict, Some(false) never, None = follow the verdict
    pub forced: Option<bool>,
}

/// Folds a new start into the executable's record (section 7: own frustum and fixed size from one start; undecided resolved by the next start).
pub fn update(prev: Option<&Record>, o: Observation) -> Record {
    let forced = prev.and_then(|r| r.forced);
    let one = judge(&o);
    if one == Verdict::OwnFrustum || one == Verdict::FixedSize {
        return Record { verdict: one, last: o, decided_by: "one start", forced };
    }
    if let Some(p) = prev {
        if let Some(v) = resolve(&p.last, &o) {
            return Record { verdict: v, last: o, decided_by: "second start", forced };
        }
        // nothing new learned: a decided honours / fixed size stays (a new restriction does not reset it)
        if p.verdict == Verdict::Honours || p.verdict == Verdict::FixedSize {
            return Record { verdict: p.verdict, last: o, decided_by: p.decided_by, forced };
        }
    }
    Record { verdict: one, last: o, decided_by: "one start", forced }
}

/// The policy at the executable's next start: restricted (told G) or told the whole view V. A never-seen game is restricted; honours stays
/// restricted; own frustum and fixed size are not; undecided is not (protect the picture), which is also the start that decides it.
pub fn restrict_next_start(r: Option<&Record>) -> bool {
    match r {
        None => true,
        Some(r) => match r.forced {
            Some(f) => f,
            None => r.verdict == Verdict::Honours,
        },
    }
}

/// The list the shim reads (HostToShim::unrestrictedExes): "|a.exe|b.exe|", lower case, the executables not restricted at their next start plus
/// the user's own list; longer than the IPC buffer (1023 UTF-16 units) is cut at the last whole entry.
pub fn unrestricted_list<'a>(records: impl Iterator<Item = (&'a str, &'a Record)>, user: &[String]) -> String {
    let mut names: Vec<String> = records.filter(|(_, r)| !restrict_next_start(Some(r))).map(|(e, _)| e.to_ascii_lowercase()).collect();
    names.extend(user.iter().map(|e| e.trim().to_ascii_lowercase()).filter(|e| !e.is_empty()));
    names.sort();
    names.dedup();
    let mut out = String::from("|");
    for n in names {
        if out.encode_utf16().count() + n.encode_utf16().count() + 1 > 1023 {
            break;
        }
        out.push_str(&n);
        out.push('|');
    }
    if out == "|" { String::new() } else { out }
}

#[cfg(test)]
mod tests {
    use super::*;

    // the lab's numbers (harness/scenarios/e2e_fov_game_*): Apple's frustum per eye L R U D, the author's fit, the 1760 x 1600 lab eye
    const P: Box2 = [[1.732, 1.0, 1.192, 1.0], [1.0, 1.732, 1.192, 1.0]];
    const FIT: Box2 = [[0.97, 0.71, 0.69, 0.63], [0.71, 0.97, 0.69, 0.63]];
    const FIT_SIZE: (i32, i32) = (1082, 964);
    const U80: Box2 = [[1.3856, 0.8, 0.9536, 0.8], [0.8, 1.3856, 0.9536, 0.8]];
    const U80_SIZE: (i32, i32) = (1408, 1280);
    const FULL: (i32, i32) = (1760, 1600);

    fn obs(told: Box2, told_size: (i32, i32), sub: Box2, size: (i32, i32)) -> Observation {
        Observation { told, told_size, submitted: sub, submitted_size: [size, size], unrestricted: false }
    }
    fn union(b: Box2) -> Box2 {
        let l = b[0][0].max(b[1][0]); let r = b[0][1].max(b[1][1]); let u = b[0][2].max(b[1][2]); let d = b[0][3].max(b[1][3]);
        [[l, r, u, d], [l, r, u, d]]
    }
    fn symmetric(b: Box2) -> Box2 {
        let tx = b[0][0].max(b[0][1]).max(b[1][0]).max(b[1][1]);
        let ty = b[0][2].max(b[0][3]).max(b[1][2]).max(b[1][3]);
        [[tx, tx, ty, ty], [tx, tx, ty, ty]]
    }

    #[test]
    fn one_start_verdicts_match_the_lab_table() {
        assert_eq!(judge(&obs(FIT, FIT_SIZE, FIT, FIT_SIZE)), Verdict::Honours);                       // e2e_fov_game_honours
        assert_eq!(judge(&obs(FIT, FIT_SIZE, union(FIT), (2160, 2160))), Verdict::OwnFrustum);         // _uncharted
        assert_eq!(judge(&obs(FIT, FIT_SIZE, symmetric(FIT), (3840, 2160))), Verdict::OwnFrustum);     // _gr2
        assert_eq!(judge(&obs(FIT, FIT_SIZE, P, FULL)), Verdict::OwnFrustum);                          // _own_fov
        assert_eq!(judge(&obs(FIT, FIT_SIZE, FIT, FULL)), Verdict::Undecided);                         // _fixed_size: 1.10 against 1.12
        assert_eq!(judge(&obs(U80, U80_SIZE, U80, FULL)), Verdict::Undecided);                         // _fixed_uniform and _res_scale
        assert_eq!(judge(&obs(FIT, FIT_SIZE, FIT, FIT_SIZE)), Verdict::Honours);                       // _lie: the known limit
        assert_eq!(judge(&obs(FIT, FIT_SIZE, FIT, (2048, 2048))), Verdict::FixedSize);                 // a square render, told FOV
        assert_eq!(judge(&obs(FIT, FIT_SIZE, FIT, (1084, 962))), Verdict::Honours);                    // rounding of 2 px
    }

    #[test]
    fn undecided_is_not_restricted_next_and_that_start_decides() {
        // first start: restricted (never seen), undecided
        assert!(restrict_next_start(None));
        let r1 = update(None, obs(U80, U80_SIZE, U80, FULL));
        assert_eq!(r1.verdict, Verdict::Undecided);
        assert!(!restrict_next_start(Some(&r1)));
        // second start told V (the stream's eye size): a fixed size does not move
        let fixed = update(Some(&r1), Observation { unrestricted: true, ..obs(P, FULL, P, FULL) });
        assert_eq!((fixed.verdict, fixed.decided_by), (Verdict::FixedSize, "second start"));
        assert!(!restrict_next_start(Some(&fixed)));
        // the same, for a 1.25 resolution scale: 1760 x 1600 told 1408 x 1280, then 2200 x 2000 told 1760 x 1600
        let scale = update(Some(&r1), Observation { unrestricted: true, ..obs(P, FULL, P, (2200, 2000)) });
        assert_eq!((scale.verdict, scale.decided_by), (Verdict::Honours, "second start"));
        assert!(restrict_next_start(Some(&scale)));
        // third start restricted again: still honours, nothing new needed
        let third = update(Some(&scale), obs(U80, U80_SIZE, U80, (1760, 1600)));
        assert_eq!(third.verdict, Verdict::Honours);
    }

    #[test]
    fn the_second_start_scenarios_three_starts() {
        // e2e_fov_game_second_start_fixed: undecided, fixed_size, and a third start told V that looks like honours on its own stays fixed_size
        let s1 = update(None, obs(U80, U80_SIZE, U80, FULL));
        let s2 = update(Some(&s1), Observation { unrestricted: true, ..obs(P, FULL, P, FULL) });
        let s3 = update(Some(&s2), Observation { unrestricted: true, ..obs(P, FULL, P, FULL) });
        assert_eq!([s1.verdict, s2.verdict, s3.verdict], [Verdict::Undecided, Verdict::FixedSize, Verdict::FixedSize]);
        assert!(!restrict_next_start(Some(&s3)));
        // e2e_fov_game_second_start_scale: undecided, honours, honours (restricted again at the third start)
        let t1 = update(None, obs(U80, U80_SIZE, U80, FULL));
        let t2 = update(Some(&t1), Observation { unrestricted: true, ..obs(P, FULL, P, (2200, 2000)) });
        let t3 = update(Some(&t2), obs(U80, U80_SIZE, U80, FULL));
        assert_eq!([t1.verdict, t2.verdict, t3.verdict], [Verdict::Undecided, Verdict::Honours, Verdict::Honours]);
        // e2e_fov_game_second_start_own: own_frustum at both starts (told V the second time, still a union)
        let o1 = update(None, obs(U80, U80_SIZE, union(U80), (2160, 2160)));
        let o2 = update(Some(&o1), Observation { unrestricted: true, ..obs(P, FULL, union(P), (2160, 2160)) });
        assert_eq!([o1.verdict, o2.verdict], [Verdict::OwnFrustum, Verdict::OwnFrustum]);
    }

    #[test]
    fn own_frustum_and_fixed_size_are_not_restricted_and_the_tick_wins() {
        let own = update(None, obs(FIT, FIT_SIZE, union(FIT), (2160, 2160)));
        assert!(!restrict_next_start(Some(&own)));
        let mut forced = own.clone();
        forced.forced = Some(true);
        assert!(restrict_next_start(Some(&forced)));
        let again = update(Some(&forced), obs(FIT, FIT_SIZE, union(FIT), (2160, 2160)));
        assert_eq!(again.forced, Some(true));   // the tick survives new starts
        let mut never = update(None, obs(FIT, FIT_SIZE, FIT, FIT_SIZE));
        never.forced = Some(false);
        assert!(!restrict_next_start(Some(&never)));
    }

    #[test]
    fn the_list_for_the_shim() {
        let own = update(None, obs(FIT, FIT_SIZE, union(FIT), (2160, 2160)));
        let hon = update(None, obs(FIT, FIT_SIZE, FIT, FIT_SIZE));
        let und = update(None, obs(U80, U80_SIZE, U80, FULL));
        let recs = vec![("Uncharted.exe".to_string(), own), ("ue4game.exe".to_string(), hon), ("GR2.exe".to_string(), und)];
        let l = unrestricted_list(recs.iter().map(|(e, r)| (e.as_str(), r)), &["Mine.EXE".to_string(), "".to_string()]);
        assert_eq!(l, "|gr2.exe|mine.exe|uncharted.exe|");
        assert_eq!(unrestricted_list(std::iter::empty(), &[]), "");
    }
}
