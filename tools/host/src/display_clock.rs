//! The headset's display period in the host's clock, measured from the tracking samples (main.rs sets the host's display grid to it).
//!
//! The grid used to run at the nominal period (10.0 ms at 100 Hz) while the display ran at its own (9.994 to 10.007 ms measured on Vision
//! Pros) and the two clocks differ by tens of ppm: a drift of up to 0.7 ms a second that the phase lock (phase.rs, steps of at most 2 ms) could
//! only chase. Measured instead, without a loop: the headset's sample timestamps are its presentation times, so whole periods between them
//! give its display period on its clock, and the lower envelope of (arrival - timestamp), one point per second over the last minute, fitted
//! against the timestamp gives the clocks' ratio (the envelope ignores Wi-Fi bunching, which only delays). Their product is the period of
//! the host's grid; the phase lock keeps only its steps. The same idea as WiVRn's and Steam Link's clock sync (docs/IDEAS_FROM_OTHER_STREAMERS.md).
//!
//! Field result (5090 + Vision Pro, 2026-11-26): the first estimate after 50 s of tracking, 9.995 to 10.003 ms, clocks -50 to +25 ppm; the
//! lock stopped sliding (it slid -0.6 to -0.8 ms/s before). A headset whose tracking is not one sample per displayed frame (the Quest 3 at
//! 270 Hz) gives no period-sized gaps: no estimate, the grid stays nominal.

use std::collections::VecDeque;

/// one estimate (ns, ppm)
#[derive(Clone, Copy, Debug)]
pub struct Estimate {
    /// the display period in the host's clock: the grid's period
    pub host_period_ns: f64,
    /// the display period in the headset's clock
    pub client_period_ns: f64,
    /// the host's clock against the headset's, ppm (positive: the host's runs faster)
    pub clock_ppm: f64,
}

impl Estimate {
    /// sane only: the period within 0.2 % of the session's (within 0.05 % all evening on the 5090) and the clocks within 500 ppm (-50 to +180 seen)
    pub fn usable(&self, nominal_ns: u64) -> bool {
        (self.host_period_ns - nominal_ns as f64).abs() / nominal_ns as f64 <= 0.002 && self.clock_ppm.abs() <= 500.0
    }
}

pub struct DisplayClock {
    prev_ts: u64,
    /// the last period-sized gaps between sample timestamps, ns
    gaps: VecDeque<u64>,
    /// (bucket start ts, lowest arrival - ts in it), ns
    bucket: Option<(u64, f64)>,
    /// one (bucket start ts, lowest offset) per second, the last minute
    points: VecDeque<(u64, f64)>,
    /// the connection's first sample: the clocks' fit starts 20 s later (start-up transient, the 5090: -4700 ppm at 36 s)
    start_ts: u64,
}

impl DisplayClock {
    pub fn new() -> Self {
        Self { prev_ts: 0, gaps: VecDeque::new(), bucket: None, points: VecDeque::new(), start_ts: 0 }
    }

    pub fn reset(&mut self) {
        *self = Self::new();
    }

    /// One tracking sample: its timestamp (the headset's clock, ns) and its arrival (the host's clock, ns), with the session's nominal
    /// period. An estimate once a second, from 50 s after the first sample on.
    pub fn push(&mut self, ts: u64, arrival_ns: f64, nominal_ns: u64) -> Option<Estimate> {
        if self.prev_ts != 0 && ts > self.prev_ts {
            let gap = ts - self.prev_ts;
            let nom = nominal_ns as f64;
            if (gap as f64) > 0.75 * nom && (gap as f64) < 1.25 * nom {
                self.gaps.push_back(gap);
                if self.gaps.len() > 6000 {
                    self.gaps.pop_front();
                }
            }
        }
        self.prev_ts = ts;
        if self.start_ts == 0 {
            self.start_ts = ts;
        }
        let off = arrival_ns - ts as f64;
        match self.bucket {
            _ if ts < self.start_ts + 20_000_000_000 => None,
            Some((b0, m)) if ts < b0 + 1_000_000_000 => {
                self.bucket = Some((b0, m.min(off)));
                None
            }
            Some((b0, m)) => {
                self.points.push_back((b0, m));
                while self.points.len() > 60 {
                    self.points.pop_front();
                }
                self.bucket = Some((ts, off));
                self.estimate(ts)
            }
            None => {
                self.bucket = Some((ts, off));
                None
            }
        }
    }

    fn estimate(&self, ts: u64) -> Option<Estimate> {
        if self.points.len() < 30 || self.gaps.len() < 1000 {
            return None;
        }
        let n = self.points.len() as f64;
        let mx = self.points.iter().map(|p| p.0 as f64 / 1e9).sum::<f64>() / n;
        let my = self.points.iter().map(|p| p.1).sum::<f64>() / n;
        let sxx: f64 = self.points.iter().map(|p| (p.0 as f64 / 1e9 - mx).powi(2)).sum();
        if sxx <= 0.0 {
            return None;
        }
        let slope = self.points.iter().map(|p| (p.0 as f64 / 1e9 - mx) * (p.1 - my)).sum::<f64>() / sxx; // ns of host per s of headset, beyond 1
        // the headset's period: the median gap (a stray gap cannot move it), then the periods counted over 5 s (that tolerates a median off by
        // 0.1 %) and over the whole window (the rig showed a mean of gaps wander by 160 ppm)
        let mut g: Vec<u64> = self.gaps.iter().copied().collect();
        g.sort_unstable();
        let med = g[g.len() / 2] as f64;
        let short = (ts - self.points[self.points.len() - 6].0) as f64;
        let p5 = short / (short / med).round().max(1.0);
        let span = (ts - self.points.front().unwrap().0) as f64;
        let client_period_ns = span / (span / p5).round().max(1.0);
        Some(Estimate { host_period_ns: client_period_ns * (1.0 + slope / 1e9), client_period_ns, clock_ppm: slope / 1e3 })
    }
}

#[cfg(test)]
mod tests {
    use super::DisplayClock;

    /// a headset at `period` ns on its clock, a host clock `ppm` faster, arrivals delayed by a network that bunches some samples
    fn run(period: f64, ppm: f64, secs: f64) -> Option<super::Estimate> {
        let mut c = DisplayClock::new();
        let mut last = None;
        let mut seed = 12345u64;
        let mut rnd = || {
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            (seed >> 33) as f64 / (1u64 << 31) as f64
        };
        let n = (secs * 1e9 / period) as u64;
        for i in 0..n {
            let ts = 5_000_000_000.0 + i as f64 * period;
            // 3 ms of base delay, up to 4 ms of jitter, one sample in 20 held 15 ms (a Wi-Fi stall)
            let delay = 3e6 + 4e6 * rnd() + if rnd() < 0.05 { 15e6 } else { 0.0 };
            let arrival = 1e9 + ts * (1.0 + ppm * 1e-6) + delay;
            if rnd() < 0.02 {
                continue; // a lost sample: a double gap
            }
            if let Some(e) = c.push(ts as u64, arrival, 10_000_000) {
                last = Some(e);
            }
        }
        last
    }

    #[test]
    fn no_estimate_in_the_first_50_seconds() {
        assert!(run(10_002_240.0, 25.0, 45.0).is_none());
    }

    #[test]
    fn finds_the_period_and_the_clocks() {
        let e = run(10_002_240.0, 25.0, 120.0).unwrap();
        assert!((e.client_period_ns - 10_002_240.0).abs() < 100.0, "{e:?}");
        assert!((e.clock_ppm - 25.0).abs() < 3.0, "{e:?}");
        assert!((e.host_period_ns - 10_002_240.0 * (1.0 + 25e-6)).abs() < 100.0, "{e:?}");
        assert!(e.usable(10_000_000));
    }

    #[test]
    fn a_slower_host_clock() {
        let e = run(9_995_130.0, -50.0, 120.0).unwrap();
        assert!((e.client_period_ns - 9_995_130.0).abs() < 100.0, "{e:?}");
        assert!((e.clock_ppm + 50.0).abs() < 3.0, "{e:?}");
    }

    #[test]
    fn a_period_far_from_the_session_is_not_usable() {
        // the headset at 90 Hz while the host still has 100 as its nominal rate (the 1.5.0 start-up case, the other way round)
        let e = run(11_111_111.0, 0.0, 120.0).unwrap();
        assert!(!e.usable(10_000_000), "{e:?}");
    }

    #[test]
    fn several_samples_per_frame_give_no_estimate() {
        // the Quest 3 tracks at 270 Hz for a 90 Hz panel
        let mut c = DisplayClock::new();
        for i in 0..100_000u64 {
            let ts = 1_000_000_000 + i * 3_703_704;
            assert!(c.push(ts, ts as f64 + 4e6, 11_111_111).is_none());
        }
    }
}
