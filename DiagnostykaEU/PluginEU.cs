using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows.Forms;

public class PluginEU
{
    public static string WersjaEU { get { return "3.44.0"; } }

    [DisplayName("Diagnostyka okna edycji")]
    public static string[] T_DiagnostykaEdycji(string zazn_xml)
    {
        try
        {
            Form target = FindTargetForm();
            if (target == null)
            {
                MessageBox.Show("Nie znaleziono otwartego okna EasyUploader. Zamknij dodatkowe komunikaty, pozostaw widoczne okno zamówienia i uruchom diagnostykę ponownie.", "Diagnostyka EasyUploader", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return new string[0];
            }

            var sb = new StringBuilder();
            sb.AppendLine("OKNO: " + target.Text);
            sb.AppendLine("TYP: " + target.GetType().FullName);
            sb.AppendLine("--- KONTROLKI ---");
            Dump(target.Controls, sb, 0);

            string text = sb.ToString();
            try { Clipboard.SetText(text); } catch { }

            using (var f = new Form())
            using (var box = new TextBox())
            using (var close = new Button())
            {
                f.Text = "ELEKTROMET - diagnostyka EasyUploader";
                f.Width = 900;
                f.Height = 650;
                f.StartPosition = FormStartPosition.CenterScreen;
                box.Multiline = true;
                box.ScrollBars = ScrollBars.Both;
                box.WordWrap = false;
                box.ReadOnly = true;
                box.Dock = DockStyle.Fill;
                box.Text = text;
                close.Text = "Zamknij";
                close.Dock = DockStyle.Bottom;
                close.Height = 38;
                close.DialogResult = DialogResult.OK;
                f.Controls.Add(box);
                f.Controls.Add(close);
                f.AcceptButton = close;
                f.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Błąd diagnostyki:\r\n" + ex.Message, "Diagnostyka EasyUploader");
        }
        return new string[0];
    }

    private static Form FindTargetForm()
    {
        Form best = null;
        int score = -1;
        foreach (Form f in Application.OpenForms)
        {
            if (f == null || !f.Visible) continue;
            int s = CountControls(f.Controls);
            string t = (f.Text ?? "").ToLowerInvariant();
            if (t.Contains("temu") && s < 20) s -= 1000;
            if (s > score) { score = s; best = f; }
        }
        return best;
    }

    private static int CountControls(Control.ControlCollection controls)
    {
        int n = 0;
        foreach (Control c in controls) { n++; if (c.HasChildren) n += CountControls(c.Controls); }
        return n;
    }

    private static void Dump(Control.ControlCollection controls, StringBuilder sb, int level)
    {
        foreach (Control c in controls)
        {
            string indent = new string(' ', level * 2);
            string text = (c.Text ?? "").Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 120) text = text.Substring(0, 120);
            sb.Append(indent).Append(c.GetType().FullName)
              .Append(" | Name=").Append(c.Name)
              .Append(" | Text=").Append(text)
              .Append(" | Enabled=").Append(c.Enabled)
              .Append(" | Visible=").Append(c.Visible)
              .AppendLine();
            if (c.HasChildren) Dump(c.Controls, sb, level + 1);
        }
    }
}
