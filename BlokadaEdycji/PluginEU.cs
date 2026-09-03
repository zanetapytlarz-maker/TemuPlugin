using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

public class PluginEU
{
    public static string WersjaEU { get { return "3.44.0"; } }
    private static bool _aktywna;
    private static Timer _timer;
    private static readonly Dictionary<DataGridView, GridGuard> _grids = new Dictionary<DataGridView, GridGuard>();

    [DisplayName("Włącz blokadę ceny i ilości")]
    public static string[] T_Wlacz_blokade_ceny_i_ilosci(object zazn)
    {
        _aktywna = true; EnsureTimer(); ApplyToOpenWindows();
        MessageBox.Show("Blokada pracownicza jest WŁĄCZONA.\n\nChronione pola: Cena jedn., Ilość i Kwota pozycji.\nPozostałe funkcje zamówienia pozostają dostępne.", "ELEKTROMET – blokada edycji", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new string[0];
    }

    [DisplayName("Wyłącz blokadę – ADMIN")]
    public static string[] T_Wylacz_blokade_ADMIN(object zazn)
    {
        string entered = PromptPin(); if (entered == null) return new string[0];
        if (!String.Equals(entered, GetAdminPin(), StringComparison.Ordinal)) { MessageBox.Show("Nieprawidłowy kod administratora.", "ELEKTROMET – blokada edycji", MessageBoxButtons.OK, MessageBoxIcon.Warning); return new string[0]; }
        _aktywna = false; RestoreAll();
        MessageBox.Show("Blokada została wyłączona na tym stanowisku.\nAby ponownie zabezpieczyć edycję, wybierz: Włącz blokadę ceny i ilości.", "ELEKTROMET – ADMIN", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new string[0];
    }

    [DisplayName("Status blokady")]
    public static string[] T_Status_blokady(object zazn) { MessageBox.Show(_aktywna ? "Blokada pracownicza: WŁĄCZONA" : "Blokada pracownicza: WYŁĄCZONA", "ELEKTROMET – status", MessageBoxButtons.OK, MessageBoxIcon.Information); return new string[0]; }

    private static void EnsureTimer() { if (_timer != null) return; _timer = new Timer(); _timer.Interval = 700; _timer.Tick += delegate { if (_aktywna) ApplyToOpenWindows(); }; _timer.Start(); }
    private static void ApplyToOpenWindows()
    {
        foreach (Form f in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (f.GetType().FullName != "EasyUploader.Features.Transakcje.Edycja.FormTransakcjeEdycja") continue;
            DataGridView grid = FindByName(f, "dataZgrupowane") as DataGridView; if (grid == null) continue;
            GridGuard guard; if (!_grids.TryGetValue(grid, out guard)) { guard = new GridGuard(grid); _grids.Add(grid, guard); } guard.Lock();
        }
    }
    private static Control FindByName(Control root, string name) { if (root.Name == name) return root; foreach (Control child in root.Controls) { Control found = FindByName(child, name); if (found != null) return found; } return null; }
    private static void RestoreAll() { foreach (GridGuard g in _grids.Values.ToArray()) g.Unlock(); }

    private sealed class GridGuard
    {
        private readonly DataGridView _grid; private readonly Dictionary<DataGridViewColumn, bool> _original = new Dictionary<DataGridViewColumn, bool>(); private bool _events;
        public GridGuard(DataGridView grid) { _grid = grid; }
        public void Lock()
        {
            if (_grid.IsDisposed) return;
            foreach (DataGridViewColumn col in _grid.Columns) if (Protected(col)) { if (!_original.ContainsKey(col)) _original[col] = col.ReadOnly; col.ReadOnly = true; }
            if (!_events) { _grid.CellBeginEdit += OnCellBeginEdit; _grid.ColumnAdded += OnColumnAdded; _events = true; }
        }
        public void Unlock()
        {
            if (_grid.IsDisposed) return;
            foreach (var kv in _original) { try { kv.Key.ReadOnly = kv.Value; } catch { } }
            if (_events) { _grid.CellBeginEdit -= OnCellBeginEdit; _grid.ColumnAdded -= OnColumnAdded; _events = false; }
        }
        private void OnColumnAdded(object sender, DataGridViewColumnEventArgs e) { if (_aktywna && Protected(e.Column)) { if (!_original.ContainsKey(e.Column)) _original[e.Column] = e.Column.ReadOnly; e.Column.ReadOnly = true; } }
        private void OnCellBeginEdit(object sender, DataGridViewCellCancelEventArgs e) { if (!_aktywna || e.ColumnIndex < 0) return; if (Protected(_grid.Columns[e.ColumnIndex])) e.Cancel = true; }
        private static bool Protected(DataGridViewColumn c)
        {
            string s = ((c.HeaderText ?? "") + " " + (c.Name ?? "") + " " + (c.DataPropertyName ?? "")).ToLowerInvariant();
            return s.Contains("cena") || s.Contains("price") || s.Contains("ilość") || s.Contains("ilosc") || s.Contains("quantity") || s.Contains("qty") || s.Contains("kwota") || s.Contains("wartość") || s.Contains("wartosc") || s.Contains("amount") || s.Contains("value");
        }
    }

    private static string GetAdminPin() { try { string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ELEKTROMET_ADMIN_PIN.txt"); if (File.Exists(path)) { string p = File.ReadAllText(path, Encoding.UTF8).Trim(); if (p.Length > 0) return p; } } catch { } return "2516"; }
    private static string PromptPin()
    {
        using (Form f = new Form()) using (TextBox box = new TextBox()) using (Button ok = new Button()) using (Button cancel = new Button()) using (Label label = new Label())
        {
            f.Text = "ELEKTROMET – administrator"; f.Width = 360; f.Height = 165; f.StartPosition = FormStartPosition.CenterScreen; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
            label.Left = 15; label.Top = 15; label.Width = 310; label.Text = "Podaj kod administratora:"; box.Left = 15; box.Top = 40; box.Width = 310; box.UseSystemPasswordChar = true;
            ok.Text = "Odblokuj"; ok.Left = 145; ok.Top = 75; ok.Width = 85; ok.DialogResult = DialogResult.OK; cancel.Text = "Anuluj"; cancel.Left = 240; cancel.Top = 75; cancel.Width = 85; cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(label); f.Controls.Add(box); f.Controls.Add(ok); f.Controls.Add(cancel); f.AcceptButton = ok; f.CancelButton = cancel; return f.ShowDialog() == DialogResult.OK ? box.Text : null;
        }
    }
}
