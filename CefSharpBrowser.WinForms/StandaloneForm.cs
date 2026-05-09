using CefSharp;
using CefSharp.WinForms;
using System;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

public class StandaloneForm : Form
{
    private readonly ChromiumWebBrowser browser;
    private readonly TextBox urlTextBox;

    public StandaloneForm(string url)
    {
        Text = "CefSharp Browser";
        Width = 1024;
        Height = 768;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 4) };
        urlTextBox = new TextBox { Width = 880, Height = 28, Text = url };
        var goButton = new Button { Text = "Go", Width = 70, Height = 28, Left = 900 };

        goButton.Click += (_, _) => Navigate();
        urlTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Navigate(); e.SuppressKeyPress = true; }
        };

        topPanel.Controls.Add(urlTextBox);
        topPanel.Controls.Add(goButton);

        browser = new ChromiumWebBrowser(url) { Dock = DockStyle.Fill };
        browser.AddressChanged += (_, e) => BeginInvoke(() => urlTextBox.Text = e.Address);

        table.Controls.Add(topPanel, 0, 0);
        table.Controls.Add(browser, 0, 1);
        Controls.Add(table);
    }

    private void Navigate()
    {
        var url = urlTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;
        browser.Load(url);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!browser.IsDisposed)
            browser.Dispose();
        base.OnFormClosing(e);
    }
}
