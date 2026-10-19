// "Find headset": the runtime window's way to (re)discover the headset on the network. VisionALVR dials the saved address first and ALVR
// re-finds a paired headset by name on its own; this dialog is for everything else: a changed address that was not picked up, a second or new
// headset, a headset that was never paired. It lists what ALVR's two discovery protocols see (mDNS for the Vision Pro, UDP 9943 broadcasts for
// Quest-style clients), lets the user take one (or only change the address of the paired one), and the runtime window then re-pairs and restarts
// the streamer. The streamer must not be running meanwhile: it owns UDP 9943.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VisionALVR
{
    public class FindHeadsetForm : DpiForm
    {
        public string ChosenHost = "", ChosenName = "", ChosenIp = "";
        readonly Tuple<string, string, string> paired;   // (hostname, name, ip) of the headset paired now, or null
        readonly ListView list = new ListView { View = View.Details, FullRowSelect = true, Height = 170, Width = 700, HideSelection = false, MultiSelect = false };
        readonly Label status = Ui.Line("");
        readonly Label fwLabel = Ui.Line("");
        readonly Button fwFix = new Button { Text = "Allow VisionALVR through the firewall (asks for administrator)", AutoSize = true, Visible = false };
        readonly Button use = new Button { Text = "Use the selected headset", AutoSize = true, Font = Ui.Bold, Enabled = false };
        readonly TextBox ipBox = new TextBox { Width = 130 };
        readonly Dictionary<string, ListViewItem> rows = new Dictionary<string, ListViewItem>();
        readonly bool autoPick;
        Discovery discovery;

        /// autoPick (test aid, `--find-auto`): after a few seconds take the first headset the network showed, else cancel.
        public FindHeadsetForm(Tuple<string, string, string> paired, bool autoPick = false)
        {
            this.paired = paired;
            this.autoPick = autoPick;
            Text = "VisionALVR \u2013 Find the headset";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);   // the pixel sizes below are designed at 96 dpi; without this nothing scales and only the fonts grow
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(12) };
            var title = Ui.Line("Find the headset on the network", Ui.Big);
            title.ForeColor = Ui.Accent;
            p.Controls.Add(title);
            p.Controls.Add(Ui.Line("Open the ALVR app on the headset (same network as this PC). It appears below within a few seconds; a headset paired before is listed at once. " +
                                   "VisionALVR normally dials the saved address and finds a paired headset again by name by itself: use this when that did not work."));
            Ui.Col(list, "Name (hostname)", 290);
            Ui.Col(list, "IP address", 120);
            Ui.Col(list, "Status", 270);
            list.SelectedIndexChanged += (s, e) => use.Enabled = list.SelectedItems.Count > 0 && ((Row)list.SelectedItems[0].Tag).Usable;
            list.DoubleClick += (s, e) => { if (use.Enabled) Accept(); };
            p.Controls.Add(list);

            var manual = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            var add = new Button { Text = paired != null ? "Change the paired headset's address" : "Add this IP address", AutoSize = true };
            add.Click += (s, e) => AddByIp();
            manual.Controls.Add(Ui.Line("Fixed address:"));
            manual.Controls.Add(ipBox);
            manual.Controls.Add(add);
            p.Controls.Add(manual);
            p.Controls.Add(status);

            var fw = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            fw.Controls.Add(fwLabel);
            fw.Controls.Add(fwFix);
            p.Controls.Add(fw);
            fwFix.Click += (s, e) => { Firewall.Allow(Paths.Host); CheckFirewall(); StartDiscovery(); };

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var again = new Button { Text = "Search again", AutoSize = true };
            again.Click += (s, e) => StartDiscovery();
            use.Click += (s, e) => Accept();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(use);
            buttons.Controls.Add(again);
            p.Controls.Add(buttons);
            Controls.Add(p);
            CancelButton = cancel;

            if (paired != null) AddRow(paired.Item1, paired.Item3, paired.Item2, "paired - saved address", true, true);
            Shown += (s, e) =>
            {
                Log.Write("INFO", "find headset: dialog open" + (autoPick ? " (auto)" : ""));
                try { CheckFirewall(); StartDiscovery(); if (autoPick) StartAutoPick(); }
                catch (Exception ex) { Log.Write("ERROR", "find headset dialog: " + ex); }
            };
            FormClosing += (s, e) => { discovery?.Dispose(); discovery = null; };
            Ui.ScreenshotWhenShown(this, 5000, null, ".dialog.png");
        }

        class Row { public string Host, Ip, Name; public bool Usable, Known, Moved; }

        static string NameFor(string host) => host.EndsWith(".client.alvr") ? "Apple Vision Pro" : host.EndsWith(".client") ? "Headset (Quest or other ALVR client)" : host;

        void StartDiscovery()
        {
            discovery?.Dispose();
            discovery = new Discovery();
            discovery.OnFound += f => BeginInvoke((Action)(() =>
            {
                Log.Write("INFO", $"find headset: seen {f.Hostname} at {f.Ip} via {f.Via}{(f.Compatible ? "" : " (incompatible)")}");
                var moved = paired != null && f.Hostname == paired.Item1 && f.Ip != paired.Item3;
                var st = !f.Compatible ? $"INCOMPATIBLE (ALVR protocol {f.Protocol}, streamer {f.ServerProtocol}): update the app on the headset"
                       : moved ? $"found at a NEW address (saved: {paired.Item3})"
                       : f.Hostname == paired?.Item1 ? "paired - found on the network"
                       : "found on the network";
                AddRow(f.Hostname, f.Ip, NameFor(f.Hostname), st, false, f.Compatible, moved);
            }));
            discovery.OnError += m => BeginInvoke((Action)(() => { status.Text = "Discovery failed: " + m; status.ForeColor = Color.Firebrick; }));
            var err = discovery.Start();
            status.Text = err ?? "Looking for headsets (Bonjour/mDNS and UDP 9943)...";
            status.ForeColor = err == null ? Color.DimGray : Color.Firebrick;
        }

        void AddRow(string host, string ip, string name, string text, bool known, bool usable, bool moved = false)
        {
            if (string.IsNullOrEmpty(ip)) return;
            ListViewItem item = null;
            foreach (var kv in rows) { var t = (Row)kv.Value.Tag; if ((host.Length > 0 && t.Host == host) || t.Ip == ip) { item = kv.Value; break; } }
            var row = new Row { Host = host, Ip = ip, Name = name, Usable = usable, Known = known, Moved = moved };
            if (item == null)
            {
                item = new ListViewItem(new[] { host.Length > 0 ? $"{name} ({host})" : name, ip, text }) { Tag = row };
                list.Items.Add(item);
                Ui.FitColumns(list);
                rows[host + "|" + ip] = item;
            }
            else
            {
                var old = (Row)item.Tag;
                if (host.Length == 0) row.Host = old.Host;
                item.Tag = row;
                item.SubItems[0].Text = row.Host.Length > 0 ? $"{name} ({row.Host})" : name;
                item.SubItems[1].Text = ip;
                item.SubItems[2].Text = text;
            }
            item.ForeColor = usable ? Color.Black : Color.Firebrick;
            if (known) Task.Run(() =>
            {
                string res;
                try { using (var pg = new Ping()) res = pg.Send(ip, 1500).Status == IPStatus.Success ? "answers ping" : "no answer to ping"; }
                catch { res = "not checked"; }
                try { BeginInvoke((Action)(() => { if (!IsDisposed && item.ListView != null) item.SubItems[2].Text = text + " - " + res; })); } catch { }
            });
            // select what the user most likely wants: a headset found at a new address, else the paired one, else the first usable
            if (moved || list.SelectedItems.Count == 0 && usable) { list.SelectedItems.Clear(); item.Selected = true; item.EnsureVisible(); }
        }

        void AddByIp()
        {
            var ip = ipBox.Text.Trim();
            if (!IPAddress.TryParse(ip, out _)) { MessageBox.Show(this, "That is not an IP address (like 192.168.1.50).", Text); return; }
            if (paired == null)
            {
                MessageBox.Show(this, "A headset that was never paired has to be found on the network first (open the ALVR app on it): its name is needed to pair it. " +
                                      "An address alone only changes where the paired headset is dialled.", Text);
                return;
            }
            AddRow(paired.Item1, ip, paired.Item2, "address entered by hand", true, true, true);
            use.Enabled = list.SelectedItems.Count > 0;
        }

        void Accept()
        {
            if (list.SelectedItems.Count == 0) return;
            var r = (Row)list.SelectedItems[0].Tag;
            if (!r.Usable) return;
            ChosenHost = r.Host; ChosenName = r.Name; ChosenIp = r.Ip;
            DialogResult = DialogResult.OK;
        }

        void StartAutoPick()
        {
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Log.Write("INFO", "find headset: auto pick " + (use.Enabled ? "accepts the selection" : "found nothing"));
                if (use.Enabled) Accept(); else DialogResult = DialogResult.Cancel;
            };
            t.Start();
        }

        void CheckFirewall()
        {
            Task.Run(() => Firewall.Check(Paths.Host)).ContinueWith(t => { try { BeginInvoke((Action)(() =>
            {
                var st = t.Result;
                fwFix.Visible = st == FirewallState.Blocked || st == FirewallState.Missing;
                fwLabel.Text = st == FirewallState.Allowed ? "Windows Firewall: alvr_host.exe in this folder is allowed."
                    : st == FirewallState.Blocked ? "Windows Firewall BLOCKS alvr_host.exe in this folder: the headset will not be found."
                    : st == FirewallState.Missing ? "Windows Firewall has no rule for alvr_host.exe in this folder: nothing will be found until it is allowed."
                    : "Windows Firewall: state unknown.";
                fwLabel.ForeColor = st == FirewallState.Allowed ? Color.SeaGreen : st == FirewallState.Unknown ? Color.DimGray : Color.Firebrick;
            })); } catch { } });
        }
    }
}
