using System;
using System.IO;
using System.Windows.Forms;

public class TemuPlugin
{
    [System.ComponentModel.DisplayName("Panel TEMU PL")]
    public static void T_Panel()
    {
        MessageBox.Show("Panel TEMU PL działa! Plugin został poprawnie załadowany.", "TEMU PL");
    }

    [System.ComponentModel.DisplayName("Pobierz nowe zamówienia")]
    public static void T_PobierzZamowienia()
    {
        MessageBox.Show("Wersja bazowa: pobieranie zamówień z TEMU będzie dodane za chwilę.", "TEMU PL");
    }

    [System.ComponentModel.DisplayName("Pakuj (TEMU)")]
    public static void T_Pakuj(string xml)
    {
        MessageBox.Show("Wersja bazowa: tutaj zostanie dodane pobieranie i drukowanie etykiety TEMU.", "TEMU PL");
    }
}
