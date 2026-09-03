using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

public class PluginEU
{
    public static string WersjaEU { get { return "3.44.0"; } }

    [DisplayName("Diagnostyka faktur Allegro")]
    public static string[] T_Diagnostyka_faktur_Allegro(object zazn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ELEKTROMET - FakturyAuto DIAG");
        sb.AppendLine("Data: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("BaseDir: " + AppDomain.CurrentDomain.BaseDirectory);
        sb.AppendLine("--- C:\\ImporterEU ---");
        try
        {
            if (Directory.Exists(@"C:\ImporterEU"))
            {
                foreach (var f in Directory.GetFiles(@"C:\ImporterEU").OrderByDescending(File.GetLastWriteTime).Take(50))
                {
                    var fi = new FileInfo(f);
                    sb.AppendLine(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + " | " + fi.Length + " | " + fi.FullName);
                }
            }
            else sb.AppendLine("Brak katalogu C:\\ImporterEU");
        }
        catch (Exception ex) { sb.AppendLine("Błąd katalogu: " + ex.Message); }

        sb.AppendLine();
        sb.AppendLine("--- ZAŁADOWANE TYPY/METODY POWIĄZANE Z ALLEGRO/FAKTURAMI ---");
        string[] keys = { "allegro", "faktur", "invoice", "checkout", "billing", "order", "dokument" };
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().OrderBy(x => x.GetName().Name))
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException rtl) { types = rtl.Types.Where(t => t != null).ToArray(); }
            catch { continue; }

            foreach (Type t in types)
            {
                bool typeHit = keys.Any(k => (t.FullName ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                catch { continue; }
                var hits = methods.Where(m => keys.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                if (!typeHit && hits.Length == 0) continue;
                sb.AppendLine();
                sb.AppendLine("[" + a.GetName().Name + "] " + t.FullName);
                foreach (var m in hits)
                {
                    string pars = "";
                    try { pars = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)); } catch { }
                    sb.AppendLine("  " + (m.IsStatic ? "static " : "") + m.ReturnType.FullName + " " + m.Name + "(" + pars + ")");
                }
                try
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        if (keys.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) sb.AppendLine("  PROP " + p.PropertyType.FullName + " " + p.Name);
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        if (keys.Any(k => f.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) sb.AppendLine("  FIELD " + f.FieldType.FullName + " " + f.Name);
                }
                catch { }
            }
        }

        string report = sb.ToString();
        string path = @"C:\ImporterEU\EasyUploader_Allegro_DIAG.txt";
        try
        {
            Directory.CreateDirectory(@"C:\ImporterEU");
            File.WriteAllText(path, report, Encoding.UTF8);
        }
        catch { path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EasyUploader_Allegro_DIAG.txt"); try { File.WriteAllText(path, report, Encoding.UTF8); } catch { } }
        try { Clipboard.SetText(report); } catch { }
        MessageBox.Show("Diagnostyka zakończona.\n\nPlik zapisano tutaj:\n" + path + "\n\nTreść została też skopiowana do schowka.", "ELEKTROMET - FakturyAuto DIAG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new string[0];
    }
}
