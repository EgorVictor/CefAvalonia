using System;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;

namespace CefSharpBrowser.WinForms
{
    public class Form1 : Form
    {
        private TextBox urlTextBox;
        private Button goButton;
        private ChromiumWebBrowser browser;

        public Form1()
        {
            InitializeComponent();
            InitializeBrowser();
        }

        private void InitializeComponent()
        {
            this.urlTextBox = new System.Windows.Forms.TextBox();
            this.goButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // urlTextBox
            // 
            this.urlTextBox.Location = new System.Drawing.Point(12, 12);
            this.urlTextBox.Name = "urlTextBox";
            this.urlTextBox.Size = new System.Drawing.Size(500, 21);
            this.urlTextBox.TabIndex = 0;
            this.urlTextBox.Text = "https://www.bing.com";
            // 
            // goButton
            // 
            this.goButton.Location = new System.Drawing.Point(518, 10);
            this.goButton.Name = "goButton";
            this.goButton.Size = new System.Drawing.Size(75, 23);
            this.goButton.TabIndex = 1;
            this.goButton.Text = "Go";
            this.goButton.UseVisualStyleBackColor = true;
            // 
            // Form1
            // 
            this.ClientSize = new System.Drawing.Size(826, 615);
            this.Controls.Add(this.urlTextBox);
            this.Controls.Add(this.goButton);
            this.Name = "Form1";
            this.Text = "CefSharp WinForms Browser";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        private void InitializeBrowser()
        {
            browser = new ChromiumWebBrowser("https://www.bing.com")
            {
                Dock = DockStyle.Fill
            };
            this.Controls.Add(browser);
            browser.AddressChanged += Browser_AddressChanged;
            browser.LoadError += Browser_LoadError;
        }

        private void GoButton_Click(object sender, EventArgs e)
        {
            NavigateToUrl();
        }

        private void UrlTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                NavigateToUrl();
            }
        }

        private void NavigateToUrl()
        {
            string url = urlTextBox.Text;
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                {
                    url = "https://" + url;
                    urlTextBox.Text = url;
                }
                browser.Load(url);
            }
        }

        private void Browser_AddressChanged(object sender, AddressChangedEventArgs e)
        {
            if (urlTextBox.InvokeRequired)
            {
                urlTextBox.Invoke(new Action(() => { urlTextBox.Text = e.Address; }));
            }
            else
            {
                urlTextBox.Text = e.Address;
            }
        }

        private void Browser_LoadError(object sender, LoadErrorEventArgs e)
        {
            if (e.ErrorCode != CefErrorCode.Aborted)
            {
                MessageBox.Show($"Page failed to load: {e.ErrorText}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Cef.IsInitialized)
            {
                var settings = new CefSettings();
                Cef.Initialize(settings);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Cef.IsInitialized)
            {
                Cef.Shutdown();
            }
            base.OnFormClosing(e);
        }
    }
}
