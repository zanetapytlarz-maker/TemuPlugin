using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

public class PluginEU
{
    private static readonly object Sync = new object();
    private static FileSystemWatcher _watcher;
    private static System.Threading.Timer _timer;
    private static bool _started;
    private static DateTime _lastEvent = DateTime.MinValue;
    private static DateTime _lastSeenWrite = DateTime.MinValue;
    private static int _processing;
    private static readonly string Dir = @"C:\ImporterEU";
    private static readonly string Pdf = @"C:\ImporterEU\Faktura.pdf";
    private static readonly string Log = @"C:\ImporterEU\FakturyAuto.log";

    public static string WersjaEU { get { StartOnce(); return "3.44.0"; } }

    [DisplayName("Faktury Allegro AUTO - status v5")]
    public static string[] T_FakturyAuto_status(object zazn)
    {
        StartOnce();
        MessageBox.Show("FakturyAuto v5 działa.\r\nLog: " + Log + "\r\nDiagnostyka: C:\\ImporterEU\\FakturyAuto_Allegro_DIAG.txt", "ELEKTROMET - FakturyAuto v5", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new[] { "UPDATE TRANSAKCJE SET ID=ID WHERE 1=0" };
    }

    [DisplayName("Faktury Allegro AUTO - test v5")]
    public static string[] T_FakturyAuto_test(object zazn)
    {
        StartOnce();
        ThreadPool.QueueUserWorkItem(_ => ProcessPdf("TEST RĘCZNY v5"));
        return new[] { "UPDATE TRANSAKCJE SET ID=ID WHERE 1=0" };
    }

    private static void StartOnce()
    {
        lock (Sync)
        {
            if (_started) return;
            _started = true;
            try
            {
                Directory.CreateDirectory(Dir);
                WriteLog("START v5 NATIVE/DIAG. BaseDir=" + AppDomain.CurrentDomain.BaseDirectory);
                DumpAllegroTypes();
                _watcher = new FileSystemWatcher(Dir, "Faktura.pdf");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += OnPdf;
                _watcher.Created += OnPdf;
                _watcher.Renamed += OnPdfRenamed;
                _watcher.EnableRaisingEvents = true;
                _timer = new System.Threading.Timer(_ => Poll(), null, 5000, 5000);
            }
            catch (Exception ex) { WriteLog("BŁĄD START: " + ex); }
        }
    }

    private static void OnPdf(object s, FileSystemEventArgs e)
    {
        _lastEvent = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER " + e.ChangeType); });
    }
    private static void OnPdfRenamed(object s, RenamedEventArgs e)
    {
        _lastEvent = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER Renamed"); });
    }
    private static void Poll()
    {
        try
        {
            if (!File.Exists(Pdf)) return;
            var t = File.GetLastWriteTime(Pdf);
            if (t > _lastSeenWrite && (DateTime.Now - t).TotalMinutes < 15)
            {
                _lastSeenWrite = t;
                if ((DateTime.Now - _lastEvent).TotalSeconds > 4) ProcessPdf("POLL v5");
            }
        }
        catch { }
    }

    private static void ProcessPdf(string reason)
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1) return;
        try
        {
            if (!File.Exists(Pdf) || !WaitReady(Pdf, 20)) return;
            var fi = new FileInfo(Pdf);
            if (fi.Length < 1000) return;
            string xmlPath = FindMatchingXml(fi.LastWriteTime);
            if (xmlPath == null) { WriteLog("STOP: brak pasującego XML"); return; }
            string xml = File.ReadAllText(xmlPath, Encoding.UTF8);
            int idTrans = GetInt(xml, "ID");
            string account = GetText(xml, "Konto");
            string marketCountry = GetText(xml, "MarketKraj");
            string checkout = GetText(xml, "AllFodId");
            string email = GetNestedText(xml, "DaneKlienta", "Email");
            byte[] pdf = File.ReadAllBytes(Pdf);
            string invoiceNo = FindInvoiceNumberFromPdf(pdf);
            if (String.IsNullOrWhiteSpace(invoiceNo)) invoiceNo = "FS-AUTO-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            WriteLog("NOWY PDF v5: " + fi.Length + " B | XML=" + Path.GetFileName(xmlPath) + " | ID_TRANS=" + idTrans + " | checkout=" + checkout + " | konto=" + account + " | kraj=" + marketCountry + " | numer=" + invoiceNo + " | " + reason);
            if (idTrans <= 0 || String.IsNullOrWhiteSpace(account)) { WriteLog("STOP: brak ID/konta"); return; }
            NativeSend(account, marketCountry, email, idTrans, invoiceNo, pdf);
            WriteLog("NATIVE RETURN OK: metoda EasyUploader zakończyła się bez wyjątku. checkout=" + checkout);
        }
        catch (TargetInvocationException tie) { WriteLog("BŁĄD NATIVE: " + (tie.InnerException ?? tie)); }
        catch (Exception ex) { WriteLog("BŁĄD PROCESS: " + ex); }
        finally { Interlocked.Exchange(ref _processing, 0); }
    }

    private static void NativeSend(string account, string country, string email, int idTrans, string invoiceNo, byte[] pdf)
    {
        Assembly a = EuAsm(); if (a == null) throw new Exception("Brak EasyUploader assembly");
        object trans = LoadTransaction(a, idTrans); if (trans == null) throw new Exception("Nie wczytano transakcji ID=" + idTrans);
        Type docType = a.GetType("EasyUploader.Features.Fakturowanie.FakturaDokument");
        Type fwType = a.GetType("EasyUploader.Features.Fakturowanie.FakturowanieWystawienie");
        if (docType == null || fwType == null) throw new Exception("Brak klas Fakturowanie");

        object doc = Activator.CreateInstance(docType, true);
        SetMember(doc, "NrDok", invoiceNo);
        SetMember(doc, "IdDok", invoiceNo);
        SetMember(doc, "Konto", account);
        SetMember(doc, "MarketKraj", country);
        SetMember(doc, "Email", email ?? "");
        SetEnumMember(doc, "Rodzaj", "FakturaVat");
        SetMember(doc, "WersjaRobocza", false);
        DumpObject(doc, "FakturaDokument");
        DumpObject(trans, "Transakcja");

        object transList = Activator.CreateInstance(typeof(List<>).MakeGenericType(trans.GetType()));
        transList.GetType().GetMethod("Add").Invoke(transList, new object[] { trans });
        object docList = Activator.CreateInstance(typeof(List<>).MakeGenericType(docType));
        docList.GetType().GetMethod("Add").Invoke(docList, new object[] { doc });

        object fw = Activator.CreateInstance(fwType, true);
        MethodInfo create = fwType.GetMethod("FakturowanieServiceCreate", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if (create != null)
        {
            WriteLog("NATIVE: FakturowanieServiceCreate(" + account + ")");
            create.Invoke(fw, new object[] { account });
        }
        DumpObject(fw, "FakturowanieWystawienie");

        MethodInfo send = fwType.GetMethod("DokumentWyslijMarketplace", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if (send == null) throw new Exception("Brak DokumentWyslijMarketplace");
        object postep = null;
        var ps = send.GetParameters();
        if (ps.Length >= 2)
        {
            try { if (!ps[1].ParameterType.IsAbstract && !ps[1].ParameterType.IsInterface) postep = Activator.CreateInstance(ps[1].ParameterType, true); } catch { postep = null; }
        }
        WriteLog("NATIVE: wywołuję DokumentWyslijMarketplace; NrDok=" + invoiceNo + "; IdDok=" + invoiceNo + "; kraj=" + country);
        send.Invoke(fw, new object[] { transList, postep, docList });
    }

    private static void DumpAllegroTypes()
    {
        try
        {
            Assembly a = EuAsm(); if (a == null) return;
            var sb = new StringBuilder();
            string[] names = {
                "EasyUploader.Features.Markety.Clients.AllegroClient",
                "EasyUploader.Features.OAuth2.Credentials.AllegroCredentials",
                "EasyUploader.Features.Fakturowanie.FakturowanieWystawienie",
                "EasyUploader.Features.Fakturowanie.FakturaDokument"
            };
            foreach (string n in names)
            {
                Type t = a.GetType(n); if (t == null) { sb.AppendLine("BRAK TYPE " + n); continue; }
                sb.AppendLine("TYPE " + t.FullName);
                foreach (var c in t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) sb.AppendLine("  CTOR " + c);
                foreach (var m in t.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) sb.AppendLine("  METHOD " + m);
                foreach (var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)) sb.AppendLine("  PROP " + p.PropertyType.FullName + " " + p.Name);
                foreach (var f in t.GetFields(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)) sb.AppendLine("  FIELD " + f.FieldType.FullName + " " + f.Name);
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine(Dir, "FakturyAuto_Allegro_DIAG.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { WriteLog("DIAG Allegro: " + ex.Message); }
    }

    private static void DumpObject(object o, string name)
    {
        try
        {
            if (o == null) return;
            var sb = new StringBuilder(); Type t = o.GetType(); sb.AppendLine("TYPE " + t.FullName);
            foreach (var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                string val=""; try { if (p.GetIndexParameters().Length==0) { var v=p.GetValue(o,null); if(v!=null) val=" = "+Safe(v); } } catch{} sb.AppendLine("PROP "+p.PropertyType.FullName+" "+p.Name+val);
            }
            foreach (var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                string val=""; try { var v=f.GetValue(o); if(v!=null) val=" = "+Safe(v); } catch{} sb.AppendLine("FIELD "+f.FieldType.FullName+" "+f.Name+val);
            }
            File.WriteAllText(Path.Combine(Dir, "FakturyAuto_"+name+"_DIAG.txt"), sb.ToString(), Encoding.UTF8);
        } catch { }
    }

    private static void SetMember(object o, string name, object value)
    {
        Type t=o.GetType();
        var p=t.GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic); if(p!=null&&p.CanWrite){try{p.SetValue(o,value,null);return;}catch{}}
        var f=t.GetField("<"+name+">k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic); if(f!=null){try{f.SetValue(o,value);}catch{}}
    }
    private static void SetEnumMember(object o,string name,string enumName)
    {
        Type t=o.GetType(); var p=t.GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic); if(p!=null&&p.CanWrite&&p.PropertyType.IsEnum){try{p.SetValue(o,Enum.Parse(p.PropertyType,enumName),null);}catch{}}
    }
    private static string Safe(object v){if(v is byte[])return "byte["+((byte[])v).Length+"]";string s=Convert.ToString(v);return s.Length>160?s.Substring(0,160):s;}

    private static object LoadTransaction(Assembly a,int id)
    {
        Type t=a.GetType("EasyUploader.Features.Transakcje.TransakcjeRepository");
        MethodInfo m=t==null?null:t.GetMethod("WczytajTransakcjeOrderBy",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
        if(m==null)return null; var e=m.Invoke(null,new object[]{id.ToString()}) as IEnumerable; if(e!=null)foreach(var x in e)return x; return null;
    }
    private static Assembly EuAsm(){return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x=>String.Equals(x.GetName().Name,"EasyUploader",StringComparison.OrdinalIgnoreCase));}
    private static string FindInvoiceNumberFromPdf(byte[] pdf)
    {
        try{string s=Encoding.GetEncoding(1252).GetString(pdf);var m=Regex.Match(s,@"\b(?:FS|FV)\s*[0-9]{1,6}\s*[/.-]\s*[0-9]{1,2}\s*[/.-]\s*[0-9]{4}(?:\s*[/.-]\s*[A-Za-z0-9_-]{1,8})?\b",RegexOptions.IgnoreCase);if(m.Success)return Regex.Replace(m.Value,@"\s+","");}catch{}return null;
    }
    private static string FindMatchingXml(DateTime pdfTime){try{return Directory.GetFiles(Dir,"*.xml").Select(p=>new FileInfo(p)).Where(f=>f.LastWriteTime<=pdfTime.AddMinutes(2)&&f.LastWriteTime>=pdfTime.AddMinutes(-30)).OrderByDescending(f=>f.LastWriteTime).Select(f=>f.FullName).FirstOrDefault();}catch{return null;}}
    private static string GetText(string xml,string node){try{var d=new XmlDocument();d.LoadXml(xml);var n=d.SelectSingleNode("//DaneTransStruct/"+node)??d.SelectSingleNode("//"+node);return n==null?null:(n.InnerText??"").Trim();}catch{return null;}}
    private static string GetNestedText(string xml,string parent,string node){try{var d=new XmlDocument();d.LoadXml(xml);var n=d.SelectSingleNode("//DaneTransStruct/"+parent+"/"+node);return n==null?null:(n.InnerText??"").Trim();}catch{return null;}}
    private static int GetInt(string xml,string node){int x;return Int32.TryParse(GetText(xml,node),out x)?x:0;}
    private static bool WaitReady(string p,int sec){for(int i=0;i<sec*2;i++){try{using(var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)){if(f.Length>0)return true;}}catch{Thread.Sleep(500);}}return false;}
    private static void WriteLog(string s){try{Directory.CreateDirectory(Dir);File.AppendAllText(Log,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" | "+s+Environment.NewLine,Encoding.UTF8);}catch{}}
}