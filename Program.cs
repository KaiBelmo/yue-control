namespace Spotikey;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Spotikey.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Spotikey is already running. Look for the green icon in the tray.",
                "Spotikey", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
