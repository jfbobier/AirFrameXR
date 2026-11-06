//! The FOV-restriction intake (docs/PICTURE_EDGES_DESIGN.md section 7): from what a game submitted against what it was told, the verdict, and
//! whether the next start of that executable is restricted. Pure logic (std only): main.rs reads the shim's record (ipc.h ShimToHost::gameSubmit*),
//! keeps the per-executable store (config\fov_verdicts.json) and publishes the lists the shim reads at an app's start (HostToShim::unrestrictedExes, restrictExes).
//!
//! The restriction is a bonus (owner, 2026-11-06): an app is told the whole view V at its first start, whatever the user configured, and the record of that start
//! says what it does with a box. A game that follows the box it is told is restricted from its next start (the first record of that start proves it or sends it
//! back); a game that draws its own frustum or a fixed other-aspect size stays on V; a game that scales its size (same aspect, other size) stays on V until the
//! user ticks it or trusts it (the whitelist, read by the shim: fov_restriction_whitelist.txt).
//!
//! Tests: `cargo test -p alvr_host --bins` on the builder, or without the workspace: `rustc --edition 2021 --test tools/host/src/fov_intake.rs`.

/// Tangents per eye, L R U D (the OVR convention: positive; the left eye's L is its outer edge, the right eye's L its inner one).
pub type Box2 = [[f32; 4]; 2];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Verdict {
    /// FOV and size as told (or a later start showed the size following the box): the restriction saves GPU work. Judged at a start that was told the whole view
    /// (`last.unrestricted`) it is a prediction (the restriction is tried at the next start); judged at a restricted start it is proven
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

/// Honours, proven by a start that was told a smaller box than the whole view (not only a prediction from a start on V).
pub fn proven(r: &Record) -> bool {
    r.verdict == Verdict::Honours && !r.last.unrestricted
}

/// The policy at the executable's next start, from the record alone: restricted (told G) or told the whole view V. A never-seen game is told V; honours
/// (predicted or proven) is restricted; own frustum, fixed size and undecided are not (protect the picture); the user's per-game tick wins. The shim adds the
/// whitelist (a trusted app is restricted even when it is unseen or undecided, but never against a verdict of own frustum or fixed size).
pub fn restrict_next_start(r: Option<&Record>) -> bool {
    match r {
        None => false,
        Some(r) => match r.forced {
            Some(f) => f,
            None => r.verdict == Verdict::Honours,
        },
    }
}

fn fit_list(mut names: Vec<String>) -> String {
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

/// The list the shim reads as HostToShim::restrictExes: "|a.exe|b.exe|", lower case, the executables told the restricted box at their next start (a verdict of
/// honours, or the user's tick); longer than the IPC buffer (1023 UTF-16 units) is cut at the last whole entry.
pub fn restrict_list<'a>(records: impl Iterator<Item = (&'a str, &'a Record)>) -> String {
    fit_list(records.filter(|(_, r)| restrict_next_start(Some(r))).map(|(e, _)| e.to_ascii_lowercase()).collect())
}

/// The list the shim reads as HostToShim::unrestrictedExes: the executables that must be told the whole view whatever the whitelist says: a verdict of own
/// frustum or fixed size (verified dishonest), the user's tick "never", and the user's own list.
pub fn unrestricted_list<'a>(records: impl Iterator<Item = (&'a str, &'a Record)>, user: &[String]) -> String {
    let mut names: Vec<String> = records
        .filter(|(_, r)| match r.forced {
            Some(f) => !f,
            None => r.verdict == Verdict::OwnFrustum || r.verdict == Verdict::FixedSize,
        })
        .map(|(e, _)| e.to_ascii_lowercase())
        .collect();
    names.extend(user.iter().map(|e| e.trim().to_ascii_lowercase()).filter(|e| !e.is_empty()));
    fit_list(names)
}

fn whitelist_lines(text: &str) -> impl Iterator<Item = String> + '_ {
    text.lines().map(|l| l.split('#').next().unwrap_or("").trim().to_ascii_lowercase()).filter(|l| !l.is_empty() && !l.contains('|') && l.len() < 120)
}

/// The whitelist: the shipped list (config\fov_restriction_whitelist.default.txt, the games and mods known to obey the box, replaced by an update) plus the user's own
/// (config\fov_restriction_whitelist.txt); one entry per line, '#' starts a comment; a name ending in .dll is a module loaded in the game's process (UEVRBackend.dll),
/// any other name is the game's executable; a line starting with '-' in the user's file removes an entry of the shipped one. Returned as the shim's list:
/// "|uevrbackend.dll|game.exe|", lower case.
pub fn whitelist_merge(shipped: &str, user: &str) -> String {
    let mut names: Vec<String> = whitelist_lines(shipped).filter(|l| !l.starts_with('-')).collect();
    for l in whitelist_lines(user) {
        match l.strip_prefix('-') {
            Some(rest) => { let rest = rest.trim(); names.retain(|n| n != rest) }
            None => names.push(l),
        }
    }
    fit_list(names)
}

/// One list on its own (the user's file without a shipped one)
pub fn whitelist_from_text(text: &str) -> String {
    whitelist_merge("", text)
}

/// The shipped list when the package's file is missing (a development tree): the entries that are known to obey the box.
pub const WHITELIST_DEFAULT: &str = "# AirFrameXR: games and mods trusted to obey the FOV restriction.\n\
# A trusted game is told the restricted box (\"Try to apply FOV restriction\") at its FIRST start; the restriction is still checked on that start, and a game\n\
# that does not follow the box goes back to the whole view and stays there. Games not listed here are told the whole view at their first start and get the\n\
# restriction from their next start only if they followed the box.\n\
# One entry per line: a DLL name (a mod loaded into the game: the game is trusted when that module is in its process) or a game executable.\n\
# Case does not matter; '#' starts a comment.\n\
UEVRBackend.dll\n";

/// What a new user whitelist starts with: no entries, the way to add and to remove one.
pub const WHITELIST_USER_TEMPLATE: &str = "# AirFrameXR: your own games and mods trusted to obey the FOV restriction (told the restricted box at their FIRST start, still checked).\n\
# The ones known to obey ship in fov_restriction_whitelist.default.txt (UEVRBackend.dll first); add yours here.\n\
# One entry per line: a DLL name (a mod loaded into the game) or a game executable. A line starting with - stops trusting an entry of the shipped list (-UEVRBackend.dll).\n\
# Case does not matter; '#' starts a comment.\n";

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
    fn a_first_start_is_told_the_whole_view_and_an_honest_game_is_restricted_next() {
        assert!(!restrict_next_start(None));   // never seen: V
        // an honest game on V: its size is the stream's (V), the box it was told: honours, predicted
        let r1 = update(None, Observation { unrestricted: true, ..obs(P, FULL, P, FULL) });
        assert_eq!(r1.verdict, Verdict::Honours);
        assert!(!proven(&r1));
        assert!(restrict_next_start(Some(&r1)));
        // the next start is told the fit: it follows (UEVR): proven
        let r2 = update(Some(&r1), obs(FIT, FIT_SIZE, FIT, FIT_SIZE));
        assert_eq!(r2.verdict, Verdict::Honours);
        assert!(proven(&r2));
        assert!(restrict_next_start(Some(&r2)));
    }

    #[test]
    fn a_game_that_looked_honest_on_v_but_ignores_the_smaller_box_goes_back() {
        // fixed size 1760 x 1600 = the stream's size: honours on V by coincidence; told the fit (1082 x 964) it renders the same size: fixed size
        let r1 = update(None, Observation { unrestricted: true, ..obs(P, FULL, P, FULL) });
        assert!(restrict_next_start(Some(&r1)));
        let r2 = update(Some(&r1), obs(FIT, FIT_SIZE, FIT, FULL));
        assert_eq!((r2.verdict, r2.decided_by), (Verdict::FixedSize, "second start"));
        assert!(!restrict_next_start(Some(&r2)));
        // and one that draws another frustum under the restriction
        let r3 = update(Some(&r1), obs(FIT, FIT_SIZE, union(FIT), (2160, 2160)));
        assert_eq!(r3.verdict, Verdict::OwnFrustum);
        assert!(!restrict_next_start(Some(&r3)));
    }

    #[test]
    fn dishonest_games_are_known_from_the_first_start_on_v() {
        let own = update(None, Observation { unrestricted: true, ..obs(P, FULL, union(P), (2160, 2160)) });   // Uncharted, Luke Ross's mods
        assert_eq!(own.verdict, Verdict::OwnFrustum);
        assert!(!restrict_next_start(Some(&own)));
        let sq = update(None, Observation { unrestricted: true, ..obs(P, FULL, P, (2160, 2160)) });         // EnshroudedVR's square frame: told 1.10 aspect, 1.0
        assert_eq!(sq.verdict, Verdict::FixedSize);
        assert!(!restrict_next_start(Some(&sq)));
    }

    #[test]
    fn undecided_stays_on_v_until_the_user_ticks_it_and_the_tick_decides_it() {
        // a resolution scale of 1.25 on V: 2200 x 2000 told 1760 x 1600: undecided, not restricted
        let r1 = update(None, Observation { unrestricted: true, ..obs(P, FULL, P, (2200, 2000)) });
        assert_eq!(r1.verdict, Verdict::Undecided);
        assert!(!restrict_next_start(Some(&r1)));
        // the user's tick: the next start is restricted, and its size followed the box (1.25 x 1082 x 964 = 1352 x 1205): honours
        let mut t = r1.clone();
        t.forced = Some(true);
        assert!(restrict_next_start(Some(&t)));
        let r2 = update(Some(&t), obs(FIT, FIT_SIZE, FIT, (1352, 1205)));
        assert_eq!((r2.verdict, r2.decided_by), (Verdict::Honours, "second start"));
        assert_eq!(r2.forced, Some(true));
        // a fixed size that keeps the aspect does not follow: fixed size (the tick still wins, the user chose it)
        let f2 = update(Some(&t), obs(FIT, FIT_SIZE, FIT, (2200, 2000)));
        assert_eq!(f2.verdict, Verdict::FixedSize);
        assert!(restrict_next_start(Some(&f2)));
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
    fn the_lists_for_the_shim_and_the_whitelist() {
        let own = update(None, obs(FIT, FIT_SIZE, union(FIT), (2160, 2160)));
        let hon = update(None, obs(FIT, FIT_SIZE, FIT, FIT_SIZE));
        let und = update(None, obs(U80, U80_SIZE, U80, FULL));
        let recs = vec![("Uncharted.exe".to_string(), own), ("ue4game.exe".to_string(), hon), ("GR2.exe".to_string(), und)];
        assert_eq!(restrict_list(recs.iter().map(|(e, r)| (e.as_str(), r))), "|ue4game.exe|");
        let l = unrestricted_list(recs.iter().map(|(e, r)| (e.as_str(), r)), &["Mine.EXE".to_string(), "".to_string()]);
        assert_eq!(l, "|mine.exe|uncharted.exe|");   // an undecided game is on neither list: the shim's default (V, or G when trusted)
        assert_eq!(unrestricted_list(std::iter::empty(), &[]), "");
        assert_eq!(restrict_list(std::iter::empty()), "");
        // the whitelist file: comments, blank lines, case, a stray bar
        assert_eq!(whitelist_from_text("# trusted\nUEVRBackend.dll   # UEVR\n\n  Game-Win64-Shipping.EXE\nbad|name\n"), "|game-win64-shipping.exe|uevrbackend.dll|");
        assert_eq!(whitelist_from_text(WHITELIST_DEFAULT), "|uevrbackend.dll|");
        // the shipped list and the user's: added, removed, no entry in the template
        assert_eq!(whitelist_merge(WHITELIST_DEFAULT, "MyMod.dll\nGame.exe # mine\n"), "|game.exe|mymod.dll|uevrbackend.dll|");
        assert_eq!(whitelist_merge(WHITELIST_DEFAULT, "-UEVRBackend.dll\nmymod.dll"), "|mymod.dll|");
        assert_eq!(whitelist_merge(WHITELIST_DEFAULT, WHITELIST_USER_TEMPLATE), "|uevrbackend.dll|");
        assert_eq!(whitelist_merge("", "-nothing.dll"), "");
        assert_eq!(whitelist_from_text(""), "");
    }
}
