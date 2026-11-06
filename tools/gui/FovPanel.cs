// The FOV restriction mini app of the launcher (docs/UI_SPEC.md): the tick, the list of the user's profiles (UserFovStore), the render quality, the stream fit and the pipeline preview that used to
// live in the Advanced window. Every change that needs a streamer restart closes the streamer (never starts it again) and the tick row says so while it runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class FovRestrictionPanel : FlowLayoutPanel
    {
        const int WrapWidth = 600;
        const string V = "video.";
        const string Restart = "needs streamer restart", RestartSentence = "Needs streamer restart.";
        readonly string storePath;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        readonly Label status = Ui.Line("");
        readonly Timer warnTimer = new Timer { Interval = 2000 };
        readonly List<UserFov> profiles = new List<UserFov>();
        CheckBox fovTick = new CheckBox();   // the state behind the two radio buttons (never shown): ticked = the restriction
        RadioButton stockRb, restrictRb; ComboBox profileBox; Label fovNote, fovShare, clientHint, closeWarn;
        TrackBar fovQ; FlowLayoutPanel fovQRow; Button manage;
        CheckBox fitTick; Label fitNote, pipeLine, pipeBand; FlowLayoutPanel pipeWarn;
        int[] streamEye;   // the stream's per-eye size (session.json, the user's resolution settings); null = not known yet
        double[] clientFov;   // the headset's tangents per edge (outer, inner, upper, lower), averaged over the two eyes; null = no headset seen yet
        readonly double[] testClient; readonly bool testTick; readonly int[] testEye; readonly bool testFit;   // the UI harness: a pretend headset, the restriction ticked, a stream size, the fit on
        bool loading, syncing;

        /// "Manage profiles and calibrate..." was pressed (the launcher opens configure.exe --fov and steps aside); without a handler the panel starts it itself.
        public event Action ManageRequested;

        public FovRestrictionPanel(double[] clientFovForTest = null, bool tickForTest = false, int[] streamEyeForTest = null, bool fitForTest = false, string storePathForTest = null)
        {
            testClient = clientFovForTest; testTick = tickForTest; testEye = streamEyeForTest; testFit = fitForTest; storePath = storePathForTest;
            FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Margin = new Padding(0);
            clientFov = testClient ?? AppSettings.ReadClientFov();

            // the choice: the vendor's own view (Apple, Meta: what the headset declares) or a restriction, a rectangle the game is told (the same for every headset)
            stockRb = new RadioButton { AutoSize = true, Text = "Stock vendor view (Apple, Meta): the game renders everything the headset declares", AccessibleName = "Stock vendor view", Margin = new Padding(3, 4, 3, 0) };
            restrictRb = new RadioButton { AutoSize = true, Text = "FOV restriction:", AccessibleName = "FOV restriction", Margin = new Padding(3, 8, 6, 0) };
            profileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, FormattingEnabled = true, AccessibleName = "Field of view profile", Margin = new Padding(3, 5, 8, 0) };
            profileBox.Format += (s, e) => { if (e.ListItem is UserFov u) e.Value = u.Name + "   " + string.Join(" / ", u.Tan.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture))); };
            closeWarn = new Label { Text = "⚠ streamer will be closed", AutoSize = true, ForeColor = Color.Firebrick, Font = Ui.Bold, Margin = new Padding(4, 7, 3, 0), Visible = StreamerProc.Running };
            Ui.Exclusive(stockRb, restrictRb);   // the second sits in a row panel of its own: Windows Forms would not make them one group
            stockRb.CheckedChanged += (s, e) => { if (!syncing && stockRb.Checked) { syncing = true; fovTick.Checked = false; syncing = false; } };
            restrictRb.CheckedChanged += (s, e) => { if (!syncing && restrictRb.Checked) { syncing = true; fovTick.Checked = true; syncing = false; } };
            fovTick.CheckedChanged += (s, e) => { if (!syncing) { syncing = true; restrictRb.Checked = fovTick.Checked; stockRb.Checked = !fovTick.Checked; syncing = false; } };
            var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            top.Controls.Add(restrictRb); top.Controls.Add(profileBox); top.Controls.Add(closeWarn);
            Controls.Add(stockRb); Controls.Add(top);
            stockRb.Checked = true;
            tips.SetToolTip(stockRb, "The default: the game is told the whole view the headset declares (Apple's compositor frustum, the Quest's lens window).");
            tips.SetToolTip(restrictRb, FovTip); tips.SetToolTip(profileBox, "AVP optimized, the profiles you calibrated or entered. The game is told this rectangle, never more than the headset shows. " + Restart + ".");

            fovNote = Ui.Line(""); fovNote.ForeColor = Color.DimGray; fovNote.Margin = new Padding(22, 2, 3, 0); fovNote.MaximumSize = new Size(WrapWidth, 0);
            fovShare = Ui.Line(""); fovShare.ForeColor = Color.SeaGreen; fovShare.Margin = new Padding(22, 0, 3, 2); fovShare.MaximumSize = new Size(WrapWidth, 0);
            clientHint = Ui.Line(""); clientHint.ForeColor = Color.DimGray; clientHint.Margin = new Padding(22, 0, 3, 2); clientHint.MaximumSize = new Size(WrapWidth, 0);
            Controls.Add(fovNote); Controls.Add(fovShare); Controls.Add(clientHint);

            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
            manage = new Button { Text = "Manage profiles and calibrate...", AutoSize = true, AccessibleName = "Manage profiles and calibrate", Margin = new Padding(22, 2, 8, 2) };
            manage.Click += (s, e) => { if (ManageRequested != null) ManageRequested(); else StartManager(); };
            tips.SetToolTip(manage, "Your profiles: calibrate (one minute, the headset on, once is enough), enter values by hand, check one with the FoV grid, rename, delete.");
            row.Controls.Add(manage);
            status.ForeColor = Color.SeaGreen; status.Margin = new Padding(3, 8, 3, 0); status.MaximumSize = new Size(WrapWidth, 0);
            row.Controls.Add(status);
            Controls.Add(row);

            // the render quality, the stream fit and the pipeline preview: behind a fold, so the card stays the size it was
            var more = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            fovQRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0), Visible = false };
            fovQ = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10, Width = 200, AutoSize = false, Height = 26, AccessibleName = "Game render quality" };
            fovQ.ValueChanged += (s, e) => FovQualityChanged();
            fovQRow.Controls.Add(new Label { Text = "Game render quality", AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
            fovQRow.Controls.Add(fovQ);
            tips.SetToolTip(fovQ, FovQualityTip);
            more.Controls.Add(fovQRow);
            fitTick = new CheckBox { AutoSize = true, Text = "Fit the stream to the headset's window (keep the pixel density)", AccessibleName = "Fit the stream to the headset's window" };
            tips.SetToolTip(fitTick, FitTip);
            more.Controls.Add(fitTick);
            fitNote = Ui.Line(""); fitNote.ForeColor = Color.DimGray; fitNote.Margin = new Padding(22, 0, 3, 2); fitNote.MaximumSize = new Size(WrapWidth, 0);
            more.Controls.Add(fitNote);
            var pipeTitle = Ui.Line("What the pipeline will do with these settings", Ui.Bold); pipeTitle.Margin = new Padding(3, 10, 3, 0);
            more.Controls.Add(pipeTitle);
            pipeLine = Ui.Line(""); pipeLine.ForeColor = Color.DimGray; pipeLine.Margin = new Padding(22, 2, 3, 0); pipeLine.MaximumSize = new Size(WrapWidth, 0);
            pipeBand = Ui.Line(""); pipeBand.ForeColor = Color.DimGray; pipeBand.Margin = new Padding(22, 0, 3, 0); pipeBand.MaximumSize = new Size(WrapWidth, 0);
            pipeWarn = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(22, 0, 3, 2) };
            more.Controls.Add(pipeLine); more.Controls.Add(pipeBand); more.Controls.Add(pipeWarn);
            Controls.Add(Ui.Expander("Render quality, stream fit and what the pipeline does", more, testFit || testEye != null));

            fovTick.CheckedChanged += (s, e) => FovTickChanged();
            profileBox.SelectedIndexChanged += (s, e) => ProfileChanged();
            fitTick.CheckedChanged += (s, e) => FitChanged();
            HandleCreated += (s, e) => { var f = FindForm(); if (f != null) f.Activated += (s2, e2) => { if (!loading) Reload(); }; };   // a tool (the wizard, the manager) may have changed the settings while this window was behind
            warnTimer.Tick += (s, e) => closeWarn.Visible = StreamerProc.Running;
            warnTimer.Start();
            Disposed += (s, e) => { warnTimer.Dispose(); tips.Dispose(); };
            Reload();
        }

        void StartManager()
        {
            try { Process.Start(new ProcessStartInfo(Paths.Configure, "--fov") { WorkingDirectory = Paths.Bin }); }
            catch (Exception e) { status.Text = "Could not open the profile manager: " + e.Message; status.ForeColor = Color.Firebrick; }
        }

        // ---- state <-> settings ------------------------------------------------------------------------------------------------------------------------------

        public UserFov Selected => profileBox.SelectedItem as UserFov;
        public bool Restricted => fovTick.Checked;
        public bool TickEnabled => fovTick.Enabled;
        public int ProfileCount => profiles.Count;
        public string Note => fovNote.Text;
        /// the UI harness: choose a profile as a click would (ticking the restriction when it is off)
        public void SelectForTest(string id, bool tick) { var u = profiles.FirstOrDefault(p => p.Id == id); if (u != null) { profileBox.SelectedItem = u; if (tick) fovTick.Checked = true; } }

        /// Reads the files again (the manager may have changed the list; the headset may have connected since).
        public void Reload()
        {
            loading = true;
            var st = new AppSettings();
            clientFov = testClient ?? AppSettings.ReadClientFov();
            if (storePath == null)
            {
                UserFovStore.SeedFromShipped(st.HeadsetName);
                UserFovStore.KeepCurrentValues(st);
            }
            profiles.Clear();
            foreach (var u in UserFovStore.Load(storePath)) if (clientFov == null || FovValidator.Check(u.Tan, FovUnit.Tangent, clientFov).Usable) profiles.Add(u);
            var want = Json.Str(st.Root, V + "fov_profile", "");
            var cur = new[] { st.FovOuter, st.FovInner, st.FovUpper, st.FovLower };
            var pick = profiles.FirstOrDefault(u => u.Id == want)
                ?? (st.FovOverride ? profiles.FirstOrDefault(u => Enumerable.Range(0, 4).All(k => Math.Abs(u.Tan[k] - cur[k]) < 0.006)) : null)
                ?? profiles.FirstOrDefault();
            profileBox.Items.Clear(); foreach (var u in profiles) profileBox.Items.Add(u);
            if (pick != null) profileBox.SelectedItem = pick;
            bool other = st.FovOverride && clientFov != null && !st.FovFits(clientFov);   // saved for a headset with another field of view (absolute tangents would cut this one)
            bool on = testTick || (st.FovOverride && !other);
            streamEye = testEye ?? ReadStreamEye();
            fovQ.Value = (int)Math.Round(st.FovRenderQuality * 100);
            fitTick.Enabled = clientFov != null;
            fitTick.Checked = clientFov != null && (testFit || st.StreamFitWindow);
            fovTick.Enabled = profiles.Count > 0;   // a profile can be chosen before any headset was seen (the cap at the headset's box is applied when it is known)
            profileBox.Enabled = profiles.Count > 0;
            fovTick.Checked = on && fovTick.Enabled;
            restrictRb.Enabled = fovTick.Enabled; stockRb.Enabled = true;
            loading = false;
            FovLabels();
        }

        /// the tangents the game is told: the chosen profile's, never above the headset's own
        double[] FovValues()
        {
            var t = Selected?.Tan ?? clientFov;
            return Enumerable.Range(0, 4).Select(k => clientFov != null ? Math.Min(t[k], clientFov[k]) : t[k]).ToArray();
        }

        void FovLabels()
        {
            FovLabelsText();
            fovShare.Visible = fovShare.Text.Length > 0; clientHint.Visible = clientHint.Text.Length > 0;   // an empty line would leave a gap in the card
        }

        void FovLabelsText()
        {
            var u = Selected;
            // the sharper stream: the headset app's own FoV Scale (the slider of the 1.3.5 client), set by hand to what the profile recommends; with "Fit the stream to the headset's window" ticked below, the stream then covers only that angle at the same pixel density
            clientHint.Text = u != null && u.ClientScale > 0 && fovTick.Checked ? u.ClientScale.ToString("0.##", CultureInfo.InvariantCulture) + " recommended for the FoV Scale slider in the headset's app (set it there by hand), and tick \"Fit the stream to the headset's window\" below to keep the pixel density." : "";
            if (clientFov == null)
            {
                fovNote.Text = fovTick.Checked ? "On: the game is told the chosen window (the headset has not connected yet: it is capped to the headset's own box once it has). " + RestartSentence
                                               : "Off: the stock vendor view. The headset's own field of view is not known yet: connect it once with the streamer (it is saved when it connects).";
                fovShare.Text = ""; UpdatePipeline(); return;
            }
            if (profiles.Count == 0)
            {
                fovNote.Text = "No profile yet: calibrate (one minute, the headset on) or enter values by hand.";
                fovShare.Text = ""; UpdatePipeline(); return;
            }
            var v = FovValues();
            var deg = string.Join(" / ", v.Select(x => (Math.Atan(x) * 180 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture)));
            if (!fovTick.Checked)
            {
                fovNote.Text = "Off: the game gets the headset's own field of view. " + (new AppSettings() is var cs && cs.FovOverride && !cs.FovFits(clientFov) ? "A restriction saved for a headset with another field of view is not applied to this one; tick to use the chosen profile. " : "") + RestartSentence;
                fovShare.Text = "";
                QualityLabel(1, 1);   // the slider still has room when the fit gives it a ceiling above the stream's size
                UpdatePipeline();
                return;
            }
            fovNote.Text = "On: the game is told " + deg + " degrees (outer / inner / upper / lower), never more than the headset's. " + RestartSentence;
            double w0 = clientFov[0] + clientFov[1], h0 = clientFov[2] + clientFov[3];
            double w = v[0] + v[1], h = v[2] + v[3];
            fovShare.Text = "The game renders " + (100 * w * h / (w0 * h0)).ToString("0", CultureInfo.InvariantCulture) + " % of the pixels the headset's whole view would.";
            QualityLabel(w / w0, h / h0);   // appends its line to fovShare
            UpdatePipeline();
        }

        void FovStore(AppSettings st)
        {
            if (fovTick.Checked)
            {
                if (Selected != null) UserFovStore.Apply(Selected, st, clientFov, st.HeadsetName);
                Json.Set(st.Root, V + "fov_render_quality", Math.Round(fovQ.Value / 100.0, 2));
            }
            else Json.Remove(st.Root, V + "fov_override");   // the saved values stay: ticking again brings them back
        }
        static readonly string[] FovKeys = { "fov_outer", "fov_inner", "fov_upper", "fov_lower" };

        void FovTickChanged()
        {
            if (loading) return;
            var st = new AppSettings();
            FovStore(st);
            Save(st, "field of view restriction " + (fovTick.Checked ? "on" : "off") + (fovTick.Checked && Selected != null ? " (" + Selected.Name + ")" : ""), Restart);
            FovLabels();
        }

        void ProfileChanged()
        {
            if (loading || Selected == null) return;
            var st = new AppSettings();
            if (fovTick.Checked) { FovStore(st); Save(st, "field of view profile " + Selected.Name, Restart); }
            else { Json.Set(st.Root, V + "fov_profile", Selected.Id); try { st.Save(); } catch { } }   // only remembered: nothing changes for the streamer
            FovLabels();
        }

        void FovQualityChanged()
        {
            if (loading || clientFov == null) return;
            FovLabels();
            if (!fovTick.Checked && !fitTick.Checked) return;
            var st = new AppSettings();
            if (fovTick.Checked) FovStore(st); else FovStoreQuality(st);
            Save(st, "game render quality " + fovQ.Value + " %", Restart);
        }

        /// Writes the settings; a change that needs a streamer restart closes the streamer (the window never starts it again: the user may go on and starts it by hand).
        void Save(AppSettings st, string what, string when)
        {
            try { st.Save(); }
            catch (Exception e) { status.Text = "Cannot write config\\airframexr.json: " + e.Message; status.ForeColor = Color.Firebrick; return; }
            Log.Write("INFO", "fov: " + what + " (" + when + ")");
            // the foveation follows the choice: centred on the restriction window (or the whole frame when it is off), at the same bitrate; a choice counts as explicit, so numbers set by hand give way
            if (testEye == null && testClient == null && storePath == null) PipelineModel.PictureEdgesFoveation.ApplyToFiles(st, true);
            if (StreamerProc.Running && testEye == null && testClient == null)
            {
                var host = FindForm(); if (host != null) host.UseWaitCursor = true;
                try { StreamerProc.Close(); } finally { if (host != null) host.UseWaitCursor = false; }
                status.Text = StreamerProc.Running ? "Saved. The streamer did not close: close it yourself." : "Saved. The streamer was closed; start it again when you are done.";
                status.ForeColor = StreamerProc.Running ? Color.Firebrick : Color.SeaGreen;
                closeWarn.Visible = StreamerProc.Running;
            }
            else { status.Text = "Saved. " + RestartSentence; status.ForeColor = Color.SeaGreen; }
        }

        // ---- moved from the Advanced window: the quality row, the stream fit and the pipeline preview ----------------------------------------------------------

        const string FovTip =
                "Field of view the game is told\r\n" +
                "\r\n" +
                "What it is: the field of view the game is told it has, taken from one of your profiles (a calibration you made, or values you entered: Manage profiles). Unticked, the game gets the headset's own, as it reported it. Ticked, it gets the profile's window, never more than the headset shows.\r\n" +
                "What you get: the game renders a smaller view with proportionally fewer pixels, so a GPU-bound game gets faster; you give up what was in the margin you trimmed. The edges of the Vision Pro's view are optically weaker than its centre, so a little off the edges costs little to see. Like SteamVR's Video > Field of View sliders, in absolute values.\r\n" +
                "What does not change: the stream. The frame still covers the headset's whole view (the headset maps it over the field of view it declared), the trimmed margin is black, encode, decode and network cost are the same, frame generation and the client are untouched. The saving is the game's GPU work. If the headset's own app also has a FoV scale, the two multiply: the headset's values shown here already include it.\r\n" +
                "\r\n" +
                "How games get it: through the OpenXR handshake. xrLocateViews returns the four angles per eye and xrEnumerateViewConfigurationViews the recommended image size; AirFrameXR returns the restricted angles and a size reduced in proportion to the area. UEVR reads exactly these (the four angles per eye for its projection and the first view's recommended size, scaled by its own resolution slider) when it starts, so it renders the narrower view. Other OpenXR games are expected to behave the same way (not tested here).\r\n" +
                "When it applies: NEEDS STREAMER RESTART (the window closes the streamer when you change it; start it again when you are done). The runtime reads the field of view once when the game starts and UEVR does not re-read it, so a game that is already running keeps what it began with, and until the streamer is restarted you may still see the old picture (for example black margins you just unticked).\r\n" +
                "\r\n" +
                "The Vision Pro's field of view as visionOS reports it (left eye): tangents 1.78 outer, 1.02 inner, 1.22 up, 1.02 down = 60.7, 45.5, 50.6, 45.5 degrees (about 106 x 96 degrees). Share of the pixels the game renders, for example: outer 1.60 and upper 1.10 -> 88.5 %; every edge 90 % of the headset's -> 81 %; 85 % -> 72 %. The fields need the headset's values: connect the headset once (they are saved when it connects).";

        const string FovQualityTip =
                "Game render quality\r\n" +
                "\r\n" +
                "What it is: how many pixels the game renders per eye, between two limits worked out from your settings. Left end: the stream's pixel density over the smaller view you chose (the fewest pixels, the biggest GPU saving). Right end: the stream's own per-eye size (the pixels an unrestricted game renders).\r\n" +
                "What you get: the pixels the restriction saved can be spent on a finer render. The game's picture is filtered down to the stream's pixels, so you get cleaner edges and less shimmer (like supersampling); you do not get detail beyond what the stream carries.\r\n" +
                "What it costs: game GPU time and video memory, rising with the pixels shown. Encode, decode and network are unchanged.\r\n" +
                "The limits follow your resolution settings (the per-eye size of the stream, e.g. a Quest's lower one) and the field of view fields above; the slider only works while a restriction is on.\r\n" +
                "When it applies: needs streamer restart, like the field of view.";

        const string FitTip =
                "Fit the stream to the headset's window\r\n" +
                "\r\n" +
                "What it is: the stream's per-eye size follows the box the headset declares, at the pixel density you chose. The density reference is the resolution in Settings (e.g. 3552 x 3200) over the box the headset declared when you saved it; it is kept in config\\airframexr.json (stream_reference_eye / stream_reference_fov). When the headset declares a smaller box (the AirFrameXR client declaring the calibrated window instead of the whole frustum), the stream shrinks in the same proportion: 3552 x 3200 over 0.94 / 0.93 / 0.90 / 0.76 becomes 3168 x 2496 over 0.96 / 0.71 / 0.69 / 0.61.\r\n" +
                "What you get: every stage after the game works on fewer pixels at the same sharpness: the compose pass, frame generation, the encoder, the network at equal bits per pixel, the headset's decoder. Measured on the lab GPU: encode time -25 % for -30 % pixels, frame generation -30 %. With the full-frame encode profile the encoded frame itself is 30 % smaller.\r\n" +
                "What does not change: what you see (the headset shows the same window), the game's own cost (it already renders only the window it is told).\r\n" +
                "When it applies: the size is written to config\\session.json when the headset connects (its declared box arrives then) and taken at the next streamer start; the streamer window says so in its footer.\r\n" +
                "The render quality slider: with the fit on its right end is the size in Settings, so the game can render the window at up to that size (supersampling, filtered down to the stream).";

        /// The stream's per-eye size from the user's resolution settings (session.json: the negotiated cache the host reads too), null when unknown.
        static int[] ReadStreamEye()
        {
            try
            {
                var s = Session.Load();
                int w = (int)Json.Num(s, "openvr_config.eye_resolution_width", 0), h = (int)Json.Num(s, "openvr_config.eye_resolution_height", 0);
                return w > 0 && h > 0 ? new[] { w, h } : null;
            }
            catch { return null; }
        }

        /// the slider's right end: the size in Settings (the density reference) when the fit is on and it is larger than the stream's size, else the stream's size
        int[] CeilingEye()
        {
            var st = new AppSettings();
            var r = st.StreamReferenceEye;
            if (streamEye != null && (testFit || st.StreamFitWindow) && r != null && (r[0] > streamEye[0] || r[1] > streamEye[1])) return new[] { Math.Max(r[0], streamEye[0]), Math.Max(r[1], streamEye[1]) };
            return streamEye;
        }

        /// the render quality row: floor and ceiling from the stream's size, the restriction and the fit, the size at the slider; hidden when floor and ceiling are the same size
        void QualityLabel(double wr, double hr)
        {
            var ceil = CeilingEye();
            bool show = streamEye != null && ceil != null;
            int[] floor = null, now = null;
            if (show)
            {
                floor = PipelineModel.GameRenderSize(new double[] { streamEye[0] * wr, streamEye[1] * hr }, new double[] { ceil[0], ceil[1] }, 0);
                now = PipelineModel.GameRenderSize(new double[] { streamEye[0] * wr, streamEye[1] * hr }, new double[] { ceil[0], ceil[1] }, fovQ.Value / 100.0);
                show = floor[0] != ceil[0] || floor[1] != ceil[1];
            }
            fovQRow.Visible = show;   // not read back from Visible: it is false until the window is shown
            if (!show) return;
            double mpx(int[] z) { return z[0] * (double)z[1] / 1e6; }
            fovShare.Text += (fovShare.Text.Length > 0 ? "\r\n" : "") + "In that view the game renders " + now[0] + " \u00D7 " + now[1] + " per eye (" + mpx(now).ToString("0.0", CultureInfo.InvariantCulture) + " Mpx, " + (100 * mpx(now) / mpx(new[] { streamEye[0], streamEye[1] })).ToString("0", CultureInfo.InvariantCulture)
                + " % of the stream's own size). Left end of the slider: " + floor[0] + " \u00D7 " + floor[1] + ", right end: " + ceil[0] + " \u00D7 " + ceil[1] + (ceil == streamEye ? " (the stream's own size)." : " (the size in Settings).");
        }

        /// the preview block: the stage sizes and the warnings for the settings as they stand in this window (the override fields, the slider, the fit) and the files (encoder, session)
        void UpdatePipeline()
        {
            if (pipeLine == null) return;
            pipeWarn.Controls.Clear();
            var st = new AppSettings();
            if (clientFov == null || streamEye == null)
            {
                pipeLine.Text = "Not known yet: connect the headset once with the streamer (its field of view and the stream size are saved then).";
                pipeBand.Text = ""; fitNote.Text = "";
                return;
            }
            Dictionary<string, object> session;
            try { session = Session.Load(); } catch { session = new Dictionary<string, object>(); }
            var i = new PipelineModel.Inputs { Declared = clientFov, Eye = streamEye, ReferenceEye = st.StreamReferenceEye, ReferenceFov = st.StreamReferenceFov, Fit = fitTick.Checked, Quality = fovQ.Value / 100.0 };
            if (fovTick.Checked) i.Rect = FovValues().Select((x, k) => Math.Min(x, clientFov[k])).ToArray();
            PipelineModel.ReadEncoder(st, session, i);
            if (testEye != null && i.Engines < 0) i.Engines = 3;   // the UI harness: a pretend GPU with three engines, so the strip line shows a number
            var r = PipelineModel.Compute(i);
            pipeLine.Text = PipelineModel.Summary(r);
            pipeBand.Text = PipelineModel.BandText(r);
            foreach (var w in r.Warnings) { var l = Ui.Line(w); l.ForeColor = Color.Firebrick; l.MaximumSize = new Size(WrapWidth, 0); l.Margin = new Padding(0, 0, 3, 0); pipeWarn.Controls.Add(l); }
            foreach (var w in r.Info) { var l = Ui.Line(w); l.ForeColor = Color.DimGray; l.MaximumSize = new Size(WrapWidth, 0); l.Margin = new Padding(0, 0, 3, 0); pipeWarn.Controls.Add(l); }
            foreach (Control c in pipeWarn.Controls) Ui.LateScale(c, this);
            var refEye = st.StreamReferenceEye; var refFov = st.StreamReferenceFov;
            if (!fitTick.Checked)
                fitNote.Text = refEye != null ? "Off: the stream is " + streamEye[0] + " \u00D7 " + streamEye[1] + " per eye whatever the headset declares. Ticking sizes it from " + refEye[0] + " \u00D7 " + refEye[1] + " over the box the headset declared when that was saved."
                    : "Off. Ticking saves the current " + streamEye[0] + " \u00D7 " + streamEye[1] + " over the headset's current box as the density to keep.";
            else if (refEye != null && refFov != null)
                fitNote.Text = "On: " + r.FitEye[0] + " \u00D7 " + r.FitEye[1] + " per eye over the headset's box (" + string.Join(" / ", clientFov.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture))) + "), from " + refEye[0] + " \u00D7 " + refEye[1 ] + " over " + string.Join(" / ", refFov.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture))) + ". Taken at the next streamer start.";
            else fitNote.Text = "On, but no density reference is saved yet: save a resolution in Settings or untick and tick again.";
        }

        /// the fit tick: saves the density reference if there is none, writes the fit size (or the reference size back) into session.json, and the switch
        void FitChanged()
        {
            if (loading || clientFov == null) return;
            var st = new AppSettings();
            if (fitTick.Checked)
            {
                if ((st.StreamReferenceEye == null || st.StreamReferenceFov == null) && streamEye != null) { st.StreamReferenceEye = streamEye; st.StreamReferenceFov = clientFov; }
                st.StreamFitWindow = true;
                if (st.StreamReferenceEye != null && st.StreamReferenceFov != null)
                {
                    var fe = PipelineModel.FitEye(st.StreamReferenceEye, st.StreamReferenceFov, clientFov);
                    var eye = new[] { Math.Min(fe[0], PipelineModel.MaxEyeWidth), Math.Min(fe[1], PipelineModel.MaxEyeHeight) };
                    st.StreamFitEye = eye;
                    WriteSessionEye(eye);
                }
            }
            else
            {
                st.StreamFitWindow = false;
                st.StreamFitEye = null;
                if (st.StreamReferenceEye != null) WriteSessionEye(st.StreamReferenceEye);
            }
            FovStoreQuality(st);
            Save(st, "stream fit " + (fitTick.Checked ? "on" : "off"), Restart);
            streamEye = testEye ?? ReadStreamEye();
            FovLabels();
        }

        void WriteSessionEye(int[] eye)
        {
            if (testEye != null) return;   // the UI harness: nothing written
            try { var s = Session.Load(); PipelineModel.WriteStreamEye(s, eye[0], eye[1]); Json.Save(Paths.Session, s); }
            catch (Exception e) { status.Text = "Cannot write config\\session.json: " + e.Message; status.ForeColor = Color.Firebrick; }
        }

        /// the slider's value is kept whenever it has a meaning: with the override on, or with the fit giving it a ceiling
        void FovStoreQuality(AppSettings st)
        {
            if (fovTick.Checked || fitTick.Checked) Json.Set(st.Root, V + "fov_render_quality", Math.Round(fovQ.Value / 100.0, 2));
        }
    }
}
