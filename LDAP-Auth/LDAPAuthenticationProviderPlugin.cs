using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LDAP_Auth.Api.Models;
using MediaBrowser.Common;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace Jellyfin.Plugin.LDAP_Auth
{
    /// <summary>
    /// Ldap Authentication Provider Plugin.
    /// </summary>
    public class LdapAuthenticationProviderPlugin : IAuthenticationProvider, IPasswordResetProvider
    {
        private readonly ILogger<LdapAuthenticationProviderPlugin> _logger;
        private readonly IApplicationHost _applicationHost;

        /// <summary>
        /// Initializes a new instance of the <see cref="LdapAuthenticationProviderPlugin"/> class.
        /// </summary>
        /// <param name="applicationHost">Instance of the <see cref="IApplicationHost"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{LdapAuthenticationProviderPlugin}"/> interface.</param>
        public LdapAuthenticationProviderPlugin(IApplicationHost applicationHost, ILogger<LdapAuthenticationProviderPlugin> logger)
        {
            _logger = logger;
            _applicationHost = applicationHost;
        }

        private string[] LdapUsernameAttributes => LdapPlugin.Instance.Configuration.LdapSearchAttributes.Replace(" ", string.Empty, StringComparison.Ordinal).Split(',');

        private string UsernameAttr => LdapPlugin.Instance.Configuration.LdapUsernameAttribute;

        private string SearchFilter => LdapPlugin.Instance.Configuration.LdapSearchFilter;

        private string AdminFilter => LdapPlugin.Instance.Configuration.LdapAdminFilter;

        /// <summary>
        /// Gets plugin name.
        /// </summary>
        public string Name => "LDAP-Authentication";

        /// <summary>
        /// Gets a value indicating whether gets plugin enabled.
        /// </summary>
        public bool IsEnabled => true;

        /// <summary>
        /// Authenticate user against the ldap server.
        /// </summary>
        /// <param name="username">Username to authenticate.</param>
        /// <param name="password">Password to authenticate.</param>
        /// <returns>A <see cref="ProviderAuthenticationResult"/> with the authentication result.</returns>
        /// <exception cref="AuthenticationException">Exception when failing to authenticate.</exception>
        public async Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            var userManager = _applicationHost.Resolve<IUserManager>();
            User user = null;
            var ldapUser = LocateLdapUser(username);
            var ldapUsername = GetAttribute(ldapUser, UsernameAttr)?.StringValue;
            _logger.LogDebug("Setting username: {LdapUsername}", ldapUsername);

            try
            {
                user = userManager.GetUserByName(ldapUsername);
            }
            catch (Exception e)
            {
                _logger.LogWarning("User Manager could not find a user for LDAP User, this may not be fatal", e);
            }

            using (var currentUserConnection = ConnectToLdap(ldapUser.Dn, password))
            {
                if (!currentUserConnection.Bound)
                {
                    _logger.LogError("Error logging in, invalid LDAP username or password");
                    throw new AuthenticationException("Error completing LDAP login. Invalid username or password.");
                }
            }

            // Determine if the user should be an administrator
            var ldapIsAdmin = false;

            if (!string.IsNullOrEmpty(AdminFilter) && !string.Equals(AdminFilter, "_disabled_", StringComparison.Ordinal))
            {
                using var ldapClient = ConnectToLdap();

                ldapClient.Constraints = GetSearchConstraints(
                    ldapClient,
                    LdapPlugin.Instance.Configuration.LdapBindUser,
                    LdapPlugin.Instance.Configuration.LdapBindPassword);

                try
                {
                    var adminBaseDn = LdapPlugin.Instance.Configuration.LdapAdminBaseDn;
                    if (string.IsNullOrEmpty(adminBaseDn))
                    {
                        adminBaseDn = LdapPlugin.Instance.Configuration.LdapBaseDn;
                    }

                    var ldapUsers = ldapClient.Search(
                        adminBaseDn,
                        LdapConnection.ScopeSub,
                        AdminFilter.Replace("{username}", username, StringComparison.OrdinalIgnoreCase),
                        Array.Empty<string>(),
                        false);

                    var foundUser = false;
                    while (ldapUsers.HasMore() && !foundUser)
                    {
                        var currentUser = ldapUsers.Next();
                        var dn = currentUser.Dn;
                        _logger.LogInfo("Admin checking: {Dn}", dn);
                        if (string.Equals(ldapUser.Dn, dn, StringComparison.Ordinal))
                        {
                            ldapIsAdmin = true;
                            foundUser = true;
                        }
                    }
                }
                catch (LdapException e)
                {
                    _logger.LogError(e, "Failed to check for admin with: {Filter}", SearchFilter);
                    throw new AuthenticationException("Error completing LDAP login while applying admin filter.");
                }
            }

            if (user == null)
            {
                _logger.LogDebug("Creating new user {Username} - is admin? {IsAdmin}", ldapUsername, ldapIsAdmin);
                if (LdapPlugin.Instance.Configuration.CreateUsersFromLdap)
                {
                    user = await userManager.CreateUserAsync(ldapUsername).ConfigureAwait(false);
                    var providerName = GetType().FullName!;
                    user.AuthenticationProviderId = providerName;
                    user.PasswordResetProviderId = providerName;
                    user.SetPermission(PermissionKind.IsAdministrator, ldapIsAdmin);
                    user.SetPermission(PermissionKind.EnableAllFolders, LdapPlugin.Instance.Configuration.EnableAllFolders);
                    if (!LdapPlugin.Instance.Configuration.EnableAllFolders)
                    {
                        user.SetPreference(PreferenceKind.EnabledFolders, LdapPlugin.Instance.Configuration.EnabledFolders);
                    }

                    await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogError("User not configured for LDAP Uid: {LdapUsername}", ldapUsername);
                    throw new AuthenticationException(
                        $"Automatic User Creation is disabled and there is no Jellyfin user for authorized Uid: {ldapUsername}");
                }
            }
            else
            {
                // User exists; if the admin has enabled an AdminFilter, check if the user's
                // 'IsAdministrator' matches the LDAP configuration and update if there is a difference.
                if (!string.IsNullOrEmpty(AdminFilter) && !string.Equals(AdminFilter, "_disabled_", StringComparison.Ordinal))
                {
                    var isJellyfinAdmin = user.HasPermission(PermissionKind.IsAdministrator);
                    if (isJellyfinAdmin != ldapIsAdmin)
                    {
                        _logger.LogDebug("Updating user {Username} admin status to: {LdapIsAdmin}.", ldapUsername, ldapIsAdmin);
                        user.SetPermission(PermissionKind.IsAdministrator, ldapIsAdmin);
                        await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    }
                }
            }

            return new ProviderAuthenticationResult { Username = ldapUsername };
        }

        /// <inheritdoc />
        public bool HasPassword(User user)
        {
            return true;
        }

        /// <summary>
        /// Changes the users password (Requires privileged bind user).
        /// </summary>
        /// <param name="user">The user who's password will be changed.</param>
        /// <param name="newPassword">The new password to set.</param>
        /// <returns>Completed Task notification.</returns>
        /// <exception cref="NotImplementedException">Thrown if AllowPassChange set to false.</exception>
        /// <exception cref="InvalidOperationException">Thrown if LdapPasswordAttribute field is null or empty.</exception>
        public Task ChangePassword(User user, string newPassword)
        {
            if (!LdapPlugin.Instance.Configuration.AllowPassChange)
            {
                throw new NotImplementedException();
            }

            if (string.IsNullOrEmpty(LdapPlugin.Instance.Configuration.LdapPasswordAttribute))
            {
                throw new InvalidOperationException("Password attribute is not set");
            }

            var passAttr = LdapPlugin.Instance.Configuration.LdapPasswordAttribute;
            var ldapUser = LocateLdapUser(user.Username);
            using var ldapClient = ConnectToLdap();
            var ldapAttr = new LdapAttribute(passAttr, newPassword);
            var ldapMod = new LdapModification(LdapModification.Replace, ldapAttr);
            ldapClient.Modify(ldapUser.Dn, ldapMod);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Deligate for validating LDAP server cert against a user provided CA.
        /// </summary>
        /// <param name="sender">An object that contains state information for this validation.</param>
        /// <param name="certificate">TLS certificate provided by the server.</param>
        /// <param name="chain">TLS chain provided by the server.</param>
        /// <param name="sslPolicyErrors">Bitset of possible policy errors with the server's certificate.</param>
        /// <returns>True if server cert is valid and trusted by the CA, otherwise false.</returns>
        private bool LdapClient_UserDefinedServerCertValidationDelegate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                || sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                _logger.LogWarning("Provided certificate not valid for remote name");
                return false;
            }

            using var rootChain = new X509Chain
            {
                ChainPolicy =
                {
                    VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority
                                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                                        | X509VerificationFlags.IgnoreCtlNotTimeValid
                                        | X509VerificationFlags.IgnoreCtlSignerRevocationUnknown
                                        | X509VerificationFlags.IgnoreEndRevocationUnknown,
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                }
            };
            rootChain.ChainPolicy.CustomTrustStore.ImportFromPemFile(LdapPlugin.Instance.Configuration.LdapRootCaPath);
            using var cert = new X509Certificate2(certificate);
            var result = rootChain.Build(cert);
            foreach (var error in chain.ChainStatus)
            {
                _logger.LogWarning("{State}: {Information}", error.Status.ToString(), error.StatusInformation);
            }

            return result;
        }

        /// <summary>
        /// Deligate for skipping TLS validation.
        /// </summary>
        /// <param name="sender">An object that contains state information for this validation.</param>
        /// <param name="certificate">TLS certificate provided by the server.</param>
        /// <param name="chain">TLS chain provided by the server.</param>
        /// <param name="sslPolicyErrors">Bitset of possible policy errors with the server's certificate.</param>
        /// <returns>True.</returns>
        private static bool LdapClient_IgnoreCertDelegate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors)
            => true;

        /// <summary>
        /// Deligate for selecting which client cert to provide to the LDAP server for client auth.
        /// </summary>
        /// <param name="sender">An object that contains state information for this validation.</param>
        /// <param name="host">Hostname you are connecting to.</param>
        /// <param name="localCerts">Local client certs.</param>
        /// <param name="remoteCert">Certificate provided by the remote party.</param>
        /// <param name="issuers">Valid certificate issuers for the remote party.</param>
        /// <returns>First client cert if available, otherwise remoteCert.</returns>
        private static X509Certificate LdapClient_CertificateSelectorDelegate(
            object sender,
            string host,
            X509CertificateCollection localCerts,
            X509Certificate remoteCert,
            string[] issuers)
        {
            if (localCerts.Count > 0)
            {
                return localCerts[0];
            }

            return remoteCert;
        }

        /// <summary>
        /// Returns the user search results for the provided filter.
        /// </summary>
        /// <param name="filter">The LDAP filter to search on.</param>
        /// <returns>The user DNs from the search results.</returns>
        /// <exception cref="AuthenticationException">Thrown on failure to connect or bind to LDAP server.</exception>
        /// <exception cref="LdapException">Thrown on failure to execute the search.</exception>
        public IEnumerable<string> GetFilteredUsers(string filter)
        {
            using var ldapClient = ConnectToLdap();

            ldapClient.Constraints = GetSearchConstraints(
                ldapClient,
                LdapPlugin.Instance.Configuration.LdapBindUser,
                LdapPlugin.Instance.Configuration.LdapBindPassword);

            try
            {
                var ldapUsers = ldapClient.Search(
                    LdapPlugin.Instance.Configuration.LdapBaseDn,
                    LdapConnection.ScopeSub,
                    filter,
                    LdapUsernameAttributes,
                    false);

                // ToList to ensure enumeration is complete before the connection is closed
                return ldapUsers.Select(u => u.Dn).ToList();
            }
            catch (LdapException e)
            {
                _logger.LogWarning(e, "Failed to filter users with: {Filter}", filter);
                throw;
            }
        }

        /// <summary>
        /// Attempts to locate the requested username on the ldap using the plugin-configured search and attribute settings.
        /// </summary>
        /// <param name="username">The username to search.</param>
        /// <returns>The located user or null if not found.</returns>
        /// <exception cref="AuthenticationException">Thrown on failure to connect or bind to LDAP server.</exception>
        public LdapEntry LocateLdapUser(string username)
        {
            var foundUser = false;
            LdapEntry ldapUser = null;
            using var ldapClient = ConnectToLdap();

            if (!ldapClient.Connected)
            {
                return null;
            }

            ldapClient.Constraints = GetSearchConstraints(
                ldapClient,
                LdapPlugin.Instance.Configuration.LdapBindUser,
                LdapPlugin.Instance.Configuration.LdapBindPassword);

            ILdapSearchResults ldapUsers;
            try
            {
                ldapUsers = ldapClient.Search(
                    LdapPlugin.Instance.Configuration.LdapBaseDn,
                    LdapConnection.ScopeSub,
                    SearchFilter,
                    LdapUsernameAttributes,
                    false);
            }
            catch (LdapException e)
            {
                _logger.LogError(e, "Failed to filter users with: {Filter}", SearchFilter);
                throw new AuthenticationException("Error completing LDAP login while applying user filter.");
            }

            _logger.LogDebug("Search: {BaseDn} {SearchFilter} @ {LdapServer}", LdapPlugin.Instance.Configuration.LdapBaseDn, SearchFilter, LdapPlugin.Instance.Configuration.LdapServer);

            var usernameComparison = LdapPlugin.Instance.Configuration.EnableCaseInsensitiveUsername
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            while (ldapUsers.HasMore() && foundUser == false)
            {
                var currentUser = ldapUsers.Next();
                foreach (var attr in LdapUsernameAttributes)
                {
                    var toCheck = GetAttribute(currentUser, attr);
                    if (toCheck?.StringValueArray != null)
                    {
                        foreach (var name in toCheck.StringValueArray)
                        {
                            if (string.Equals(username, name, usernameComparison))
                            {
                                ldapUser = currentUser;
                                foundUser = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (ldapUser == null)
            {
                _logger.LogError("Found no users matching {Username} in LDAP search", username);
                throw new AuthenticationException("Found no LDAP users matching provided username.");
            }

            return ldapUser;
        }

        /// <inheritdoc />
        public Task<ForgotPasswordResult> StartForgotPasswordProcess(User user, bool isInNetwork)
        {
            var resetUrl = LdapPlugin.Instance.Configuration.PasswordResetUrl;
            if (string.IsNullOrEmpty(resetUrl))
            {
                throw new NotImplementedException();
            }

            resetUrl = resetUrl
                .Replace("$userId", user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("$userName", user.Username, StringComparison.OrdinalIgnoreCase);

            var result = new ForgotPasswordResult
            {
                Action = ForgotPasswordAction.PinCode,
                PinFile = resetUrl
            };

            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public Task<PinRedeemResult> RedeemPasswordResetPin(string pin)
        {
            throw new NotImplementedException();
        }

        private LdapAttribute GetAttribute(LdapEntry userEntry, string attr)
        {
            var attributeSet = userEntry.GetAttributeSet();
            if (attributeSet.ContainsKey(attr))
            {
                return attributeSet.GetAttribute(attr);
            }

            _logger.LogWarning("LDAP attribute {Attr} not found for user {User}", attr, userEntry.Dn);
            return null;
        }

        private LdapConnectionOptions GetConnectionOptions()
        {
            var connectionOptions = new LdapConnectionOptions();
            var configuration = LdapPlugin.Instance.Configuration;
            if (configuration.UseSsl)
            {
                connectionOptions.UseSsl();
            }

            if (configuration.SkipSslVerify)
            {
                connectionOptions.ConfigureRemoteCertificateValidationCallback(LdapClient_IgnoreCertDelegate);
            }
            else if (!string.IsNullOrEmpty(configuration.LdapRootCaPath))
            {
                connectionOptions.ConfigureRemoteCertificateValidationCallback(LdapClient_UserDefinedServerCertValidationDelegate);
            }

            if (!string.IsNullOrEmpty(configuration.LdapClientCertPath) && !string.IsNullOrEmpty(configuration.LdapClientKeyPath))
            {
                var cert = X509Certificate2.CreateFromPemFile(configuration.LdapClientCertPath, configuration.LdapClientKeyPath);
                connectionOptions.ConfigureClientCertificates(new[] { cert });
                connectionOptions.ConfigureLocalCertificateSelectionCallback(LdapClient_CertificateSelectorDelegate);
            }

            return connectionOptions;
        }

        private LdapSearchConstraints GetSearchConstraints(
            LdapConnection ldapClient, string dn, string password)
        {
            var constraints = ldapClient.SearchConstraints;
            constraints.ReferralFollowing = true;
            constraints.setReferralHandler(new LdapAuthHandler(_logger, dn, password));
            return constraints;
        }

        private LdapConnection ConnectToLdap(string userDn = null, string userPassword = null)
        {
            bool initialConnection = userDn == null;
            if (initialConnection)
            {
                userDn = LdapPlugin.Instance.Configuration.LdapBindUser;
                userPassword = LdapPlugin.Instance.Configuration.LdapBindPassword;
            }

            // not using `using` for the ability to return ldapClient, need to dispose this manually on exception
            var ldapClient = new LdapConnection(GetConnectionOptions());
            try
            {
                ldapClient.Connect(LdapPlugin.Instance.Configuration.LdapServer, LdapPlugin.Instance.Configuration.LdapPort);
                if (LdapPlugin.Instance.Configuration.UseStartTls)
                {
                    ldapClient.StartTls();
                }

                _logger.LogDebug("Trying bind as user {UserDn}", userDn);
                ldapClient.Bind(userDn, userPassword);
            }
            catch (Exception e)
            {
                ldapClient.Dispose();

                _logger.LogError(e, "Failed to Connect or Bind to server as user {UserDn}", userDn);
                var message = initialConnection
                    ? "Failed to Connect or Bind to server."
                    : "Error completing LDAP login. Invalid username or password.";
                throw new AuthenticationException(message);
            }

            return ldapClient;
        }

        /// <summary>
        /// Tests the server connection and bind settings.
        /// </summary>
        /// <returns>A string reporting the result of the sequence of connection steps.</returns>
        public ServerTestResponse TestServerBind()
        {
            const string Started = "Testing...";
            const string Success = "Success";

            var configuration = LdapPlugin.Instance.Configuration;
            var connectionOptions = GetConnectionOptions();
            var response = new ServerTestResponse();

            try
            {
                response.Connect = Started;
                using var ldapClient = new LdapConnection(connectionOptions);
                ldapClient.Connect(configuration.LdapServer, configuration.LdapPort);
                response.Connect = Success;

                if (configuration.UseStartTls)
                {
                    response.StartTls = Started;
                    ldapClient.StartTls();
                    response.StartTls = Success;
                }

                response.Bind = Started;
                ldapClient.Bind(configuration.LdapBindUser, configuration.LdapBindPassword);
                response.Bind = ldapClient.Bound ? Success : "Anonymous";

                response.BaseSearch = Started;
                var entries = ldapClient.Search(
                    configuration.LdapBaseDn,
                    LdapConnection.ScopeSub,
                    string.Empty,
                    Array.Empty<string>(),
                    false);

                // entries.Count is unreliable (timing issue?), iterate to count
                var count = 0;
                while (entries.HasMore())
                {
                    entries.Next();
                    count++;
                }

                response.BaseSearch = $"Found {count} Entities";
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Ldap Test Failed to Connect or Bind to server");
                response.Error = e.Message;
            }

            return response;
        }
    }
}
