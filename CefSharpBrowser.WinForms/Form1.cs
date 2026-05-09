using CefSharp;
using CefSharp.WinForms;
using System;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

public class Form1 : Form
{
    private readonly ChromiumWebBrowser browser;
    private readonly TextBox urlTextBox;
    private readonly Button goButton;

    public Form1()
    {
        Text = "CefSharp WinForms Browser";
        Width = 1024;
        Height = 768;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5, 5, 5, 3) };
        urlTextBox = new TextBox { Width = 900, Height = 26, Text = "https://www.bing.com" };
        goButton = new Button { Text = "Go", Width = 70, Height = 26, Left = 910 };

        goButton.Click += (_, _) => Navigate();
        urlTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Navigate(); e.SuppressKeyPress = true; }
        };

        topPanel.Controls.Add(urlTextBox);
        topPanel.Controls.Add(goButton);

        browser = new ChromiumWebBrowser("about:blank")
        {
            Dock = DockStyle.Fill
        };
        browser.AddressChanged += (_, e) => urlTextBox.Text = e.Address;

        panel.Controls.Add(topPanel, 0, 0);
        panel.Controls.Add(browser, 0, 1);
        Controls.Add(panel);
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
