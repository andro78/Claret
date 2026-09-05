using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Claret.Models;
using Claret.Services;

namespace Claret.Dialogs
{
    /// <summary>
    /// Collects (or edits) the fields of a <see cref="ConnectionProfile"/> plus the one-shot secret.
    /// In edit mode the primary button saves instead of connecting.
    /// </summary>
    public sealed partial class ConnectionDialog : ContentDialog
    {
        private readonly IntPtr _ownerWindowHandle;
        private readonly ConnectionProfile _profile;
        private readonly bool _editOnly;

        public ConnectionDialog(IntPtr ownerWindowHandle, ConnectionProfile? existing = null, bool editOnly = false)
        {
            InitializeComponent();

            _ownerWindowHandle = ownerWindowHandle;
            _editOnly = editOnly;
            _profile = existing?.Clone() ?? new ConnectionProfile();

            if (editOnly)
            {
                Title = "Edit profile";
                PrimaryButtonText = "Save";
            }
            else if (existing is not null)
            {
                Title = $"Connect — {existing.DisplayName}";
            }

            SaveProfileCheck.Checked += (_, _) => RememberSecretCheck.IsEnabled = true;
            SaveProfileCheck.Unchecked += (_, _) =>
            {
                RememberSecretCheck.IsEnabled = false;
                RememberSecretCheck.IsChecked = false;
            };

            PrimaryButtonClick += OnPrimaryButtonClick;

            LoadFrom(_profile, existing is not null);
        }

        /// <summary>The profile as edited. Only meaningful after the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
        public ConnectionProfile Profile => _profile;

        /// <summary>Password or key passphrase typed by the user; null when left blank.</summary>
        public string? Secret { get; private set; }

        /// <summary>Whether the user asked for this profile to be persisted.</summary>
        public bool ShouldSaveProfile { get; private set; }

        private void LoadFrom(ConnectionProfile profile, bool isExisting)
        {
            NameBox.Text = profile.Name;
            HostBox.Text = profile.Host;
            PortBox.Value = profile.Port;
            UserBox.Text = profile.Username;
            KeyPathBox.Text = profile.PrivateKeyPath;

            AuthModeButtons.SelectedIndex = profile.AuthMode == SshAuthMode.PrivateKey ? 1 : 0;
            UpdateAuthPanels();

            if (isExisting)
            {
                SaveProfileCheck.IsChecked = true;
                RememberSecretCheck.IsEnabled = true;
                RememberSecretCheck.IsChecked = profile.RememberSecret;

                // Pre-fill the stored secret so the user can connect (or re-save) without retyping.
                string? stored = SecretProtector.Unprotect(profile.ProtectedSecret);
                if (!string.IsNullOrEmpty(stored))
                {
                    if (profile.AuthMode == SshAuthMode.PrivateKey)
                    {
                        PassphraseBoxInput.Password = stored;
                    }
                    else
                    {
                        PasswordBoxInput.Password = stored;
                    }
                }
            }
        }

        private void OnAuthModeChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

        /// <summary>
        /// A password or passphrase is almost always plain ASCII, but an IME left in Hangul (or any
        /// other script) mode still composes into a PasswordBox — Windows does not block it there the
        /// way it blocks the on-screen candidate window elsewhere. Stripping anything outside ASCII as
        /// it lands means the field types as English regardless of the system input language.
        /// </summary>
        private void OnPasswordChanging(PasswordBox sender, PasswordBoxPasswordChangingEventArgs args)
        {
            string current = sender.Password;
            string ascii = ToAsciiOnly(current);
            if (ascii != current)
            {
                sender.Password = ascii;
            }
        }

        private static string ToAsciiOnly(string text)
        {
            if (text.Length == 0)
            {
                return text;
            }

            char[] filtered = new char[text.Length];
            int count = 0;
            foreach (char c in text)
            {
                if (c < 128)
                {
                    filtered[count++] = c;
                }
            }

            return count == text.Length ? text : new string(filtered, 0, count);
        }

        private void UpdateAuthPanels()
        {
            bool usesKey = AuthModeButtons.SelectedIndex == 1;
            PasswordPanel.Visibility = usesKey ? Visibility.Collapsed : Visibility.Visible;
            KeyPanel.Visibility = usesKey ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnBrowseKeyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.ComputerFolder,
                };

                // Private keys usually have no extension, so accept everything.
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindowHandle);

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    KeyPathBox.Text = file.Path;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Cannot open the file picker: {ex.Message}");
            }
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string host = HostBox.Text.Trim();
            string user = UserBox.Text.Trim();
            bool usesKey = AuthModeButtons.SelectedIndex == 1;
            string keyPath = KeyPathBox.Text.Trim();

            if (host.Length == 0)
            {
                Reject(args, "Enter a host.", HostBox);
                return;
            }

            if (user.Length == 0)
            {
                Reject(args, "Enter a user name.", UserBox);
                return;
            }

            int port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value;
            if (port is < 1 or > 65535)
            {
                Reject(args, "Port must be between 1 and 65535.", PortBox);
                return;
            }

            if (usesKey)
            {
                if (keyPath.Length == 0)
                {
                    Reject(args, "Enter the private key file path.", KeyPathBox);
                    return;
                }

                if (!File.Exists(keyPath))
                {
                    Reject(args, "Private key file not found.", KeyPathBox);
                    return;
                }
            }

            _profile.Name = NameBox.Text.Trim();
            _profile.Host = host;
            _profile.Port = port;
            _profile.Username = user;
            _profile.AuthMode = usesKey ? SshAuthMode.PrivateKey : SshAuthMode.Password;
            _profile.PrivateKeyPath = usesKey ? keyPath : string.Empty;

            string typed = usesKey ? PassphraseBoxInput.Password : PasswordBoxInput.Password;
            Secret = typed.Length == 0 ? null : typed;

            ShouldSaveProfile = SaveProfileCheck.IsChecked == true;
            _profile.RememberSecret = ShouldSaveProfile && RememberSecretCheck.IsChecked == true;
            _profile.ProtectedSecret = _profile.RememberSecret ? SecretProtector.Protect(Secret) : null;

            ErrorBar.IsOpen = false;
        }

        private void Reject(ContentDialogButtonClickEventArgs args, string message, Control focusTarget)
        {
            args.Cancel = true;
            ShowError(message);
            focusTarget.Focus(FocusState.Programmatic);
        }

        private void ShowError(string message)
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }

        /// <summary>True when the dialog was opened purely to edit a stored profile.</summary>
        public bool IsEditOnly => _editOnly;
    }
}
