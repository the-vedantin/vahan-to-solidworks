using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

// Vahan -> SolidWorks hardpoints.
//
// ONE 3D sketch "Hardpoints" holds every point (plus a permanent ORIGIN at 0,0,0).
// Identity is a name -> persistent-reference map stored in the part as a custom
// property, so a point is re-found by the SAME persistent reference the chassis
// uses. Updates resolve each point by that reference and MOVE it in place
// (SetCoords) - the entity is never deleted/recreated, so external/PDM references
// survive. Proven: a stored persistref resolves the same point after move+rebuild.
class HP
{
    const string SK = "Hardpoints";
    const string PROP = "VAHAN_HP_MAP";
    const double MAX_MM = 5000.0;                 // sanity envelope; rejects mispastes
    static readonly string[] CORNERS = { "FL", "FR", "RL", "RR" };
    const string ORIGIN = "ORIGIN";

    static ISldWorks sw;
    static ModelDoc2 m;
    static ModelDocExtension ext;
    static SketchManager sm;
    static CustomPropertyManager cpm;

    // name -> {x,y,z} metres. Excludes N/A (0,0,0) corners. Always contains ORIGIN.
    static Dictionary<string, double[]> target = new Dictionary<string, double[]>();
    static List<string> order = new List<string>();   // stable apply order

    static int created, moved, staleN, failed;
    static List<string> staleList = new List<string>();
    static List<string> failList = new List<string>();

    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int r, out IRunningObjectTable t);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int r, out IBindCtx c);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint fmt);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    const uint CF_UNICODETEXT = 13;

    static void LogF(string s)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "VahanHardpoints.log"),
              DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); } catch { }
    }

    static string ClipboardText()
    {
        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            for (int a = 0; a < 20; a++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr h = GetClipboardData(CF_UNICODETEXT);
                        if (h == IntPtr.Zero) return null;
                        IntPtr p = GlobalLock(h);
                        if (p == IntPtr.Zero) return null;
                        try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
                    }
                    finally { CloseClipboard(); }
                }
                System.Threading.Thread.Sleep(25);
            }
        }
        catch { }
        return null;
    }

    static List<ISldWorks> AllSW()
    {
        var list = new List<ISldWorks>();
        IRunningObjectTable rot; GetRunningObjectTable(0, out rot);
        IEnumMoniker en; rot.EnumRunning(out en);
        IMoniker[] mo = new IMoniker[1]; IBindCtx c; CreateBindCtx(0, out c);
        while (en.Next(1, mo, IntPtr.Zero) == 0)
        {
            string nm; mo[0].GetDisplayName(c, null, out nm);
            if (nm != null && nm.StartsWith("SolidWorks_PID_"))
                try { object o; rot.GetObject(mo[0], out o); if (o is ISldWorks) list.Add((ISldWorks)o); } catch { }
        }
        return list;
    }
    static ISldWorks FindSW(out ModelDoc2 part)
    {
        part = null; ISldWorks first = null;
        foreach (var a in AllSW())
        {
            if (first == null) first = a;
            try { ModelDoc2 d = (ModelDoc2)a.ActiveDoc; if (d != null && d.GetType() == (int)swDocumentTypes_e.swDocPART) { part = d; return a; } } catch { }
        }
        return first;
    }

    // ---- parse + validate (all-or-nothing) ------------------------------
    static string ParseData(string raw)
    {
        target.Clear(); order.Clear();
        if (string.IsNullOrWhiteSpace(raw)) return "Nothing to apply - paste your Vahan points first.";
        string t = raw.Replace("\r", "|").Replace("\n", "|").Replace("\t", "");
        var errors = new List<string>();
        var seen = new HashSet<string>();

        // ORIGIN is always present.
        target[ORIGIN] = new double[] { 0, 0, 0 }; order.Add(ORIGIN);

        foreach (var rec in t.Split('|'))
        {
            if (rec.Trim().Length == 0) continue;
            var f = rec.Split(',');
            string nm = f.Length > 0 ? Sanitize(f[0].Trim()) : "";
            double dud;
            if (nm.Length == 0 || nm.ToLower() == "point" || double.TryParse(nm, NumberStyles.Any, CultureInfo.InvariantCulture, out dud)) continue;
            if (f.Length < 13) { errors.Add("  \"" + Trunc(rec, 42) + "\" needs 13 fields, has " + f.Length); continue; }
            if (nm == ORIGIN) { errors.Add("  \"ORIGIN\" is reserved - rename that hardpoint"); continue; }
            if (!seen.Add(nm)) { errors.Add("  duplicate hardpoint \"" + nm + "\""); continue; }

            double[] v = new double[12]; bool bad = false;
            for (int i = 0; i < 12; i++)
            {
                double x;
                if (!double.TryParse(f[i + 1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out x) ||
                    double.IsNaN(x) || double.IsInfinity(x))
                { errors.Add("  \"" + nm + "\" field " + (i + 2) + " (\"" + f[i + 1].Trim() + "\") is not a valid number"); bad = true; break; }
                if (Math.Abs(x) > MAX_MM)
                { errors.Add("  \"" + nm + "\" field " + (i + 2) + " = " + x + " mm is outside +/-" + MAX_MM + " mm"); bad = true; break; }
                v[i] = x;
            }
            if (bad) continue;

            for (int ci = 0; ci < 4; ci++)
            {
                double x = v[ci * 3], y = v[ci * 3 + 1], z = v[ci * 3 + 2];
                if (Math.Abs(x) < 1e-6 && Math.Abs(y) < 1e-6 && Math.Abs(z) < 1e-6) continue; // N/A corner
                string name = CORNERS[ci] + "_" + nm;
                target[name] = new double[] { x / 1000.0, y / 1000.0, z / 1000.0 };
                order.Add(name);
            }
        }
        if (errors.Count > 0)
            return "The paste has " + errors.Count + " problem(s) - NOTHING was applied:\r\n" + string.Join("\r\n", errors.ToArray());
        if (target.Count <= 1)
            return "No valid hardpoints found.\r\nExpected: name,FLx,FLy,FLz,FRx,FRy,FRz,RLx,RLy,RLz,RRx,RRy,RRz  (records split by | or newlines)";
        return null;
    }
    static string Trunc(string s, int n) { s = s.Trim(); return s.Length <= n ? s : s.Substring(0, n) + "..."; }
    static string Sanitize(string s)
    {
        foreach (var c in new[] { "@", "/", "\\", "<", ">", ":", "\"", "?", "*", "|", " ", ".", "#", "\t" }) s = s.Replace(c, "_");
        return s;
    }

    // ---- persistent-reference map (stored in a custom property) ----------
    static Dictionary<string, byte[]> ReadMap()
    {
        var d = new Dictionary<string, byte[]>();
        try
        {
            string val, resolved; cpm.Get4(PROP, false, out val, out resolved);
            if (string.IsNullOrEmpty(val)) return d;
            foreach (var line in val.Split('\n'))
            {
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t'); if (tab <= 0) continue;
                string name = line.Substring(0, tab);
                try { d[name] = Convert.FromBase64String(line.Substring(tab + 1)); } catch { }
            }
        }
        catch (Exception e) { LogF("ReadMap: " + e.Message); }
        return d;
    }
    static void WriteMap(Dictionary<string, byte[]> d)
    {
        var sb = new StringBuilder();
        foreach (var kv in d) sb.Append(kv.Key).Append('\t').Append(Convert.ToBase64String(kv.Value)).Append('\n');
        cpm.Add3(PROP, (int)swCustomInfoType_e.swCustomInfoText, sb.ToString(), (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
    }

    static Feature FeatByName(string nm)
    {
        Feature f = (Feature)m.FirstFeature();
        while (f != null) { if (f.Name == nm) return f; f = (Feature)f.GetNextFeature(); }
        return null;
    }
    static Sketch SketchByName(string nm)
    {
        Feature f = FeatByName(nm);
        return f == null ? null : (Sketch)f.GetSpecificFeature2();
    }
    static List<SketchPoint> RealPoints(Sketch sk)
    {
        var l = new List<SketchPoint>();
        object[] pts = (object[])sk.GetSketchPoints2();
        if (pts != null) foreach (object o in pts) { SketchPoint p = (SketchPoint)o; if (p.Type == 1) l.Add(p); }
        return l;
    }

    // ---- apply -----------------------------------------------------------
    public static string Apply(string raw, Action<int, int, string> progress)
    {
        created = moved = staleN = failed = 0; staleList.Clear(); failList.Clear();

        sw = FindSW(out m);
        if (sw == null) return "SolidWorks is not running.\r\nOpen your hardpoint part, then Apply again.";
        if (m == null) return "No part is open in SolidWorks.\r\nOpen your hardpoint part, then Apply again.";
        if (m.GetType() != (int)swDocumentTypes_e.swDocPART) return "The active document is not a part.\r\nOpen your hardpoint part, then Apply again.";
        if (m.IsOpenedReadOnly())
            return "\"" + m.GetTitle() + "\" is READ-ONLY (checked in / not checked out).\r\n\r\n" +
                   "Check it out in Kenesto (so it opens writable), then Apply again.\r\nNothing was changed.";

        string perr = ParseData(raw);
        if (perr != null) return perr;

        ext = m.Extension; sm = m.SketchManager;

        // Detect an existing per-corner named-sketch part (FL_uca_front, ...).
        // If present, update those in place by name (safe: names don't change,
        // and it preserves the linkage that references them). Otherwise use the
        // single-sketch design for a fresh part.
        int namedHit = 0;
        foreach (string nm in order)
        {
            if (nm == ORIGIN) continue;
            Feature nf = FeatByName(nm);
            if (nf != null && nf.GetTypeName2() == "3DProfileFeature") { namedHit++; if (namedHit >= 2) break; }
        }
        if (namedHit >= 2) return ApplyNamed(progress);

        cpm = (CustomPropertyManager)ext.get_CustomPropertyManager("");
        var map = ReadMap();

        Sketch sketch = SketchByName(SK);
        bool isNew = sketch == null;

        // provenance guard: a Hardpoints sketch with points but no map is not ours.
        if (map.Count == 0 && sketch != null && RealPoints(sketch).Count > 0)
            return "This part already has a \"" + SK + "\" sketch with points but no Vahan map.\r\n" +
                   "It was not built by this tool - refusing to touch it so nothing breaks.\r\n" +
                   "(If it IS meant to be managed here, delete that sketch first and re-run.)";

        // plan
        var toMove = new List<string>();
        var toCreate = new List<string>();
        foreach (string name in order)
            if (map.ContainsKey(name)) toMove.Add(name); else toCreate.Add(name);
        foreach (string name in map.Keys)
            if (!target.ContainsKey(name)) { staleList.Add(name); staleN++; }

        int total = order.Count;
        int done = 0;
        bool oldDisp = false, oldAdd = false;

        try
        {
            oldDisp = sm.DisplayWhenAdded; sm.DisplayWhenAdded = false;
            oldAdd = sm.AddToDB; sm.AddToDB = true;
            sw.CommandInProgress = true;

            // exit any sketch the part is already sitting in (e.g. an open 3DSketch1),
            // otherwise Insert3DSketch would CLOSE that instead of opening ours.
            m.ClearSelection2(true);
            for (int g = 0; g < 6 && sm.ActiveSketch != null; g++) { sm.Insert3DSketch(false); if (sm.ActiveSketch != null) sm.InsertSketch(false); }

            // open the ONE sketch
            if (isNew) { m.ClearSelection2(true); sm.Insert3DSketch(true); }
            else { m.ClearSelection2(true); m.Extension.SelectByID2(SK, "SKETCH", 0, 0, 0, false, 0, null, 0); m.EditSketch(); }
            Sketch sk = (Sketch)sm.ActiveSketch;
            if (sk == null && isNew) { sm.Insert3DSketch(true); sk = (Sketch)sm.ActiveSketch; }   // retry once
            if (sk == null) throw new Exception("could not open the Hardpoints sketch for edit (active sketch could not be started)");

            // free any fixed points so they can move (points-only sketch: safe)
            try { ((SketchRelationManager)sk.RelationManager).DeleteAllRelations(); } catch { }

            // MOVE existing points by their stored persistent reference
            foreach (string name in toMove)
            {
                double[] c = target[name];
                int err; object o = ext.GetObjectByPersistReference3(map[name], out err);
                SketchPoint p = o as SketchPoint;
                if (err == 0 && p != null) { p.SetCoords(c[0], c[1], c[2]); moved++; }
                else { toCreate.Add(name); map.Remove(name); }   // ref broken -> recreate, don't orphan
                if (progress != null) progress(++done, total, name);
            }

            // CREATE new points (append, remember order to capture refs after)
            int baseCount = RealPoints(sk).Count;
            var createdNames = new List<string>();
            foreach (string name in toCreate)
            {
                double[] c = target[name];
                SketchPoint p = sm.CreatePoint(c[0], c[1], c[2]);
                if (p != null) { createdNames.Add(name); created++; }
                else { failed++; failList.Add(name); }
                if (progress != null) progress(++done, total, name);
            }

            // lock everything: select all real points, one Fixed
            var reals = RealPoints(sk);
            m.ClearSelection2(true);
            for (int i = 0; i < reals.Count; i++) reals[i].Select4(i > 0, null);
            if (reals.Count > 0) m.SketchAddConstraints("sgFIXED");
            m.ClearSelection2(true);

            sm.Insert3DSketch(true);                 // close sketch
            if (isNew)
            {
                Feature f = (Feature)m.FirstFeature(), last = null;
                while (f != null) { string tn = f.GetTypeName2(); if (tn == "3DProfileFeature" || tn == "ProfileFeature") last = f; f = (Feature)f.GetNextFeature(); }
                if (last != null) last.Name = SK;
            }

            sw.CommandInProgress = false; sm.AddToDB = oldAdd; sm.DisplayWhenAdded = oldDisp;
            m.EditRebuild3();

            // capture persistent refs for the newly created points (stable post-close)
            Sketch sk2 = SketchByName(SK);
            var reals2 = RealPoints(sk2);
            for (int i = 0; i < createdNames.Count; i++)
            {
                int idx = baseCount + i;
                if (idx < reals2.Count)
                {
                    object blob = ext.GetPersistReference3(reals2[idx]);
                    try { map[createdNames[i]] = (byte[])blob; }
                    catch { failList.Add(createdNames[i] + " (ref capture)"); }
                }
            }
            WriteMap(map);
            if (created > 0) m.ViewZoomtofit2();
        }
        catch (Exception e)
        {
            try { if (sm.ActiveSketch != null) sm.Insert3DSketch(true); } catch { }
            try { sw.CommandInProgress = false; sm.AddToDB = oldAdd; sm.DisplayWhenAdded = oldDisp; } catch { }
            LogF("EXCEPTION: " + e.GetType().Name + ": " + e.Message);
            return "Apply failed partway:\r\n" + e.Message + "\r\n\r\n" +
                   "Do NOT save. Close the part without saving to discard, then re-run.\r\n" +
                   "(created " + created + ", moved " + moved + " before the error)";
        }

        // count assertion
        int expected = target.Count;           // includes ORIGIN, excludes N/A
        Sketch skf = SketchByName(SK);
        int actual = skf == null ? 0 : RealPoints(skf).Count;

        var sb = new StringBuilder();
        bool clean = failed == 0 && staleN == 0;
        sb.AppendLine((clean ? "OK - " : "DONE (with warnings) - ") + m.GetTitle());
        sb.AppendLine(new string('-', 50));
        sb.AppendLine("moved in place : " + moved);
        sb.AppendLine("created        : " + created);
        sb.AppendLine("origin point   : present at (0,0,0)");
        sb.AppendLine("total points   : " + actual + (actual == expected ? "  (matches paste)" : "  (EXPECTED " + expected + " !)"));
        if (staleN > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WARNING - " + staleN + " point(s) exist in the part but are NOT in this paste,");
            sb.AppendLine("so they still hold OLD values (version mix). Paste the complete set to fix:");
            foreach (string s in staleList) sb.AppendLine("   " + s);
        }
        if (failed > 0)
        {
            sb.AppendLine();
            sb.AppendLine("FAILED (" + failed + ") - re-run Apply to retry:");
            foreach (string s in failList) sb.AppendLine("   " + s);
        }
        if (clean && actual == expected)
            sb.AppendLine("\r\nEvery point matches the paste. Points moved in place - references preserved.\r\nPress Ctrl+S in SolidWorks to save (then check in via Kenesto).");
        return sb.ToString();
    }

    // In-place update for a part built as one named 3D sketch per hardpoint.
    // Moves each existing point (SetCoords) - never deletes/recreates - so the
    // linkage and any external references that use these points survive.
    static string ApplyNamed(Action<int, int, string> progress)
    {
        int total = order.Count, done = 0;
        bool oldDisp = false;
        try
        {
            oldDisp = sm.DisplayWhenAdded; sm.DisplayWhenAdded = false; sw.CommandInProgress = true;
            for (int g = 0; g < 6 && sm.ActiveSketch != null; g++) { sm.Insert3DSketch(false); if (sm.ActiveSketch != null) sm.InsertSketch(false); }

            foreach (string name in order)
            {
                double[] c = target[name];
                string sketchName = name == ORIGIN ? "ORIGIN_PT" : name;
                Feature f = FeatByName(sketchName);

                if (f == null)
                {
                    // new hardpoint (or first origin): make a named sketch with one fixed point
                    m.ClearSelection2(true); sm.AddToDB = true; sm.Insert3DSketch(true);
                    SketchPoint np = sm.CreatePoint(c[0], c[1], c[2]);
                    m.ClearSelection2(true); if (np != null) { np.Select4(false, null); m.SketchAddConstraints("sgFIXED"); }
                    m.ClearSelection2(true); sm.AddToDB = false; sm.Insert3DSketch(true);
                    Feature last = null, ff = (Feature)m.FirstFeature();
                    while (ff != null) { if (ff.GetTypeName2() == "3DProfileFeature") last = ff; ff = (Feature)ff.GetNextFeature(); }
                    if (last != null) { last.Name = sketchName; created++; } else { failed++; failList.Add(sketchName); }
                }
                else
                {
                    try
                    {
                        m.ClearSelection2(true);
                        m.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);
                        m.EditSketch();
                        Sketch sk = (Sketch)sm.ActiveSketch;
                        var reals = RealPoints(sk);
                        if (reals.Count == 0) { sm.Insert3DSketch(true); failed++; failList.Add(sketchName); }
                        else
                        {
                            try { ((SketchRelationManager)sk.RelationManager).DeleteAllRelations(); } catch { }
                            reals[0].SetCoords(c[0], c[1], c[2]);
                            m.ClearSelection2(true); reals[0].Select4(false, null); m.SketchAddConstraints("sgFIXED"); m.ClearSelection2(true);
                            sm.Insert3DSketch(true); moved++;
                        }
                    }
                    catch (Exception ex) { try { if (sm.ActiveSketch != null) sm.Insert3DSketch(true); } catch { } failed++; failList.Add(sketchName + " (" + ex.Message + ")"); }
                }
                if (progress != null) progress(++done, total, name);
            }

            sw.CommandInProgress = false; sm.DisplayWhenAdded = oldDisp; m.EditRebuild3();
        }
        catch (Exception e)
        {
            try { if (sm.ActiveSketch != null) sm.Insert3DSketch(true); } catch { }
            try { sw.CommandInProgress = false; sm.DisplayWhenAdded = oldDisp; } catch { }
            return "Update failed partway:\r\n" + e.Message + "\r\nDo NOT save; close without saving and re-run.";
        }

        var sb = new StringBuilder();
        sb.AppendLine((failed == 0 ? "OK - " : "DONE (with errors) - ") + m.GetTitle() + "  [named-sketch part]");
        sb.AppendLine(new string('-', 50));
        sb.AppendLine("moved in place : " + moved);
        if (created > 0) sb.AppendLine("created (new)   : " + created);
        if (failed > 0) { sb.AppendLine("FAILED (" + failed + "):"); foreach (string s in failList) sb.AppendLine("   " + s); }
        sb.AppendLine("ORIGIN_PT      : present at (0,0,0)");
        if (failed == 0) sb.AppendLine("\r\nEvery point moved in place - linkage and references preserved.\r\nCtrl+S to save, then check in via Kenesto.");
        return sb.ToString();
    }

    public static string ParseError(string raw) { return ParseData(raw); }
    public static int Count(string raw) { return ParseData(raw) == null ? target.Count : 0; }
    public static string ActivePart()
    {
        try { ModelDoc2 d; var a = FindSW(out d); if (d == null) return a != null ? "(no part open)" : "(SolidWorks not running)"; return d.GetTitle() + (d.IsOpenedReadOnly() ? "  [READ-ONLY - check out in Kenesto]" : ""); }
        catch { return "(unknown)"; }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string dataPath = null; bool quiet = false;
        foreach (var a in args) { if (a == "--quiet") quiet = true; else if (a.StartsWith("--data=")) dataPath = a.Substring(7); }
        if (dataPath != null)
        {
            string r = Apply(File.ReadAllText(dataPath), null);
            if (quiet) Console.WriteLine(r); else MessageBox.Show(r, "Vahan Hardpoints");
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string clip = ClipboardText();
        if (clip == null) { try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch { } }
        if (clip != null && clip.IndexOf(',') < 0) clip = null;
        Application.Run(new VahanForm(clip));
    }
}

class VahanForm : Form
{
    TextBox box, result; Button applyBtn, closeBtn; Label header, status; ProgressBar prog; bool busy;

    public VahanForm(string prefill)
    {
        Text = "Vahan Hardpoints"; Width = 780; Height = 700; StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(580, 500); Font = new Font("Segoe UI", 9f);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 56f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 44f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        header = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = 56,
            Text = "Paste your Vahan points, then Apply. Every point is set to exactly these coordinates and MOVED in place " +
                   "(references preserved). A permanent ORIGIN point at 0,0,0 is always kept. Columns: name, then 4 corners of X,Y,Z in mm." };
        box = new TextBox { Dock = DockStyle.Fill, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both,
            AcceptsReturn = true, AcceptsTab = false, Font = new Font("Consolas", 9.5f), Text = prefill ?? "" };
        box.TextChanged += (s, e) => Refresh2();
        status = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = 22, ForeColor = Color.DimGray };
        prog = new ProgressBar { Dock = DockStyle.Fill, Height = 16, Visible = false };
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
        applyBtn = new Button { Text = "Apply to Part", Width = 150, Height = 34, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        closeBtn = new Button { Text = "Close", Width = 90, Height = 34 };
        applyBtn.Click += OnApply; closeBtn.Click += (s, e) => Close();
        btnRow.Controls.Add(applyBtn); btnRow.Controls.Add(closeBtn);
        result = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f), BackColor = Color.White, Text = "Results will appear here." };
        root.Controls.Add(header, 0, 0); root.Controls.Add(box, 0, 1); root.Controls.Add(status, 0, 2);
        root.Controls.Add(prog, 0, 3); root.Controls.Add(btnRow, 0, 4); root.Controls.Add(result, 0, 5);
        Controls.Add(root); AcceptButton = null;
        Refresh2();
    }

    void Refresh2()
    {
        if (busy) return;
        string err = HP.ParseError(box.Text);
        if (string.IsNullOrWhiteSpace(box.Text)) { status.Text = "Active part: " + HP.ActivePart() + "   |   paste your points"; status.ForeColor = Color.DimGray; applyBtn.Enabled = false; return; }
        if (err != null) { status.Text = err.Split('\n')[0]; status.ForeColor = Color.Firebrick; applyBtn.Enabled = false; return; }
        int n = HP.Count(box.Text);
        status.Text = "Active part: " + HP.ActivePart() + "   |   parsed " + (n - 1) + " points + ORIGIN - ready";
        status.ForeColor = Color.SeaGreen; applyBtn.Enabled = true;
    }

    void OnApply(object sender, EventArgs e)
    {
        string err = HP.ParseError(box.Text);
        if (err != null) { result.Text = err; return; }
        int n = HP.Count(box.Text);
        if (MessageBox.Show("Set all " + n + " points (incl. ORIGIN) in \"" + HP.ActivePart() + "\" to exactly the pasted values?\r\n\r\n" +
                            "Points move in place. Cannot be interrupted once started.",
                            "Apply", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        busy = true; applyBtn.Enabled = false; closeBtn.Enabled = false; box.ReadOnly = true;
        prog.Visible = true; prog.Value = 0; result.Text = "Applying - do not touch SolidWorks..."; UseWaitCursor = true; Application.DoEvents();
        string report;
        try { report = HP.Apply(box.Text, (dn, tot, nm) => { if (prog.Maximum != tot) prog.Maximum = tot; prog.Value = Math.Min(dn, tot); status.Text = "Applying " + dn + "/" + tot + "  (" + nm + ")"; Application.DoEvents(); }); }
        catch (Exception ex) { report = "Unexpected error:\r\n" + ex.Message; }
        UseWaitCursor = false; prog.Visible = false; result.Text = report;
        result.ForeColor = report.StartsWith("OK") ? Color.Black : Color.Firebrick;
        busy = false; box.ReadOnly = false; closeBtn.Enabled = true; Refresh2();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { if (busy) { e.Cancel = true; return; } base.OnFormClosing(e); }
}
