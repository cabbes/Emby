﻿using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Security;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Net;

namespace MediaBrowser.Common.Implementations.Security
{
    /// <summary>
    /// Class PluginSecurityManager
    /// </summary>
    public class PluginSecurityManager : ISecurityManager
    {
        private const string MBValidateUrl = MbAdmin.HttpsUrl + "service/registration/validate";
        private const string AppstoreRegUrl = /*MbAdmin.HttpsUrl*/ "http://mb3admin.com/admin/service/appstore/register";

        /// <summary>
        /// The _is MB supporter
        /// </summary>
        private bool? _isMbSupporter;
        /// <summary>
        /// The _is MB supporter initialized
        /// </summary>
        private bool _isMbSupporterInitialized;
        /// <summary>
        /// The _is MB supporter sync lock
        /// </summary>
        private object _isMbSupporterSyncLock = new object();

        /// <summary>
        /// Gets a value indicating whether this instance is MB supporter.
        /// </summary>
        /// <value><c>true</c> if this instance is MB supporter; otherwise, <c>false</c>.</value>
        public bool IsMBSupporter
        {
            get
            {
                LazyInitializer.EnsureInitialized(ref _isMbSupporter, ref _isMbSupporterInitialized, ref _isMbSupporterSyncLock, () => GetRegistrationStatus().Result.IsRegistered);
                return _isMbSupporter.Value;
            }
        }

        private MBLicenseFile _licenseFile;
        private MBLicenseFile LicenseFile
        {
            get { return _licenseFile ?? (_licenseFile = new MBLicenseFile(_appPaths)); }
        }

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationHost _appHost;
        private readonly IApplicationPaths _appPaths;

        private IEnumerable<IRequiresRegistration> _registeredEntities;
        protected IEnumerable<IRequiresRegistration> RegisteredEntities
        {
            get
            {
                return _registeredEntities ?? (_registeredEntities = _appHost.GetExports<IRequiresRegistration>());
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginSecurityManager" /> class.
        /// </summary>
        public PluginSecurityManager(IApplicationHost appHost, IHttpClient httpClient, IJsonSerializer jsonSerializer,
            IApplicationPaths appPaths, ILogManager logManager)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException("httpClient");
            }

            _appHost = appHost;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _appPaths = appPaths;
        }

        /// <summary>
        /// Load all registration info for all entities that require registration
        /// </summary>
        /// <returns></returns>
        public async Task LoadAllRegistrationInfo()
        {
            var tasks = new List<Task>();

            ResetSupporterInfo();
            tasks.AddRange(RegisteredEntities.Select(i => i.LoadRegistrationInfoAsync()));
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Gets the registration status.
        /// This overload supports existing plug-ins.
        /// </summary>
        /// <param name="feature">The feature.</param>
        /// <param name="mb2Equivalent">The MB2 equivalent.</param>
        /// <returns>Task{MBRegistrationRecord}.</returns>
        public Task<MBRegistrationRecord> GetRegistrationStatus(string feature, string mb2Equivalent = null)
        {
            return GetRegistrationStatus();
        }

        /// <summary>
        /// Gets the registration status.
        /// </summary>
        /// <param name="feature">The feature.</param>
        /// <param name="mb2Equivalent">The MB2 equivalent.</param>
        /// <param name="version">The version of this feature</param>
        /// <returns>Task{MBRegistrationRecord}.</returns>
        public Task<MBRegistrationRecord> GetRegistrationStatus(string feature, string mb2Equivalent, string version)
        {
            return GetRegistrationStatus();
        }

        private async Task<MBRegistrationRecord> GetRegistrationStatus()
        {
            return new MBRegistrationRecord
+            {
+                IsRegistered = true,
+                RegChecked = true,
+                TrialVersion = false,
+                IsValid = true,
+                RegError = false
+            };
         }

        /// <summary>
        /// Gets or sets the supporter key.
        /// </summary>
        /// <value>The supporter key.</value>
        public string SupporterKey
        {
            get
            {
                return LicenseFile.RegKey;
            }
            set
            {
                if (value != LicenseFile.RegKey)
                {
                    LicenseFile.RegKey = value;
                    LicenseFile.Save();

                    // re-load registration info
                    Task.Run(() => LoadAllRegistrationInfo());
                }
            }
        }

        public async Task<SupporterInfo> GetSupporterInfo()
        {
            var key = SupporterKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                return new SupporterInfo();
            }

            var data = new Dictionary<string, string>
                {
                    { "key", key }, 
                };

            var url = MbAdmin.HttpsUrl + "/service/supporter/retrieve";

            using (var stream = await _httpClient.Post(url, data, CancellationToken.None).ConfigureAwait(false))
            {
                var response = _jsonSerializer.DeserializeFromStream<SuppporterInfoResponse>(stream);

                var info = new SupporterInfo
                {
                    Email = response.email,
                    PlanType = response.planType,
                    SupporterKey = response.supporterKey,
                    IsActiveSupporter = IsMBSupporter
                };

                if (!string.IsNullOrWhiteSpace(response.expDate))
                {
                    DateTime parsedDate;
                    if (DateTime.TryParse(response.expDate, out parsedDate))
                    {
                        info.ExpirationDate = parsedDate;
                    }
                    else
                    {

                    }
                }

                if (!string.IsNullOrWhiteSpace(response.regDate))
                {
                    DateTime parsedDate;
                    if (DateTime.TryParse(response.regDate, out parsedDate))
                    {
                        info.RegistrationDate = parsedDate;
                    }
                    else
                    {
                    }
                }

                info.IsExpiredSupporter = info.ExpirationDate.HasValue && info.ExpirationDate < DateTime.UtcNow && !string.IsNullOrWhiteSpace(info.SupporterKey);

                return info;
            }
        }

        /// <summary>
        /// Register an app store sale with our back-end.  It will validate the transaction with the store
        /// and then register the proper feature and then fill in the supporter key on success.
        /// </summary>
        /// <param name="parameters">Json parameters to send to admin server</param>
        public async Task RegisterAppStoreSale(string parameters)
        {
            var options = new HttpRequestOptions()
            {
                Url = AppstoreRegUrl,
                CancellationToken = CancellationToken.None
            };
            options.RequestHeaders.Add("X-Emby-Token", _appHost.SystemId);
            options.RequestContent = parameters;
            options.RequestContentType = "application/json";

            try
            {
                using (var response = await _httpClient.Post(options).ConfigureAwait(false))
                {
                    var reg = _jsonSerializer.DeserializeFromStream<RegRecord>(response.Content);

                    if (reg == null)
                    {
                        var msg = "Result from appstore registration was null.";
                        throw new ApplicationException(msg);
                    }
                    if (!String.IsNullOrEmpty(reg.key))
                    {
                        SupporterKey = reg.key;
                    }
                }

            }
            catch (ApplicationException)
            {
                SaveAppStoreInfo(parameters);
                throw;
            }
            catch (HttpException e)
            {
                _logger.ErrorException("Error registering appstore purchase {0}", e, parameters ?? "NO PARMS SENT");

                if (e.StatusCode.HasValue && e.StatusCode.Value == HttpStatusCode.PaymentRequired)
                {
                    throw new PaymentRequiredException();
                }
                throw new ApplicationException("Error registering store sale");
            }
            catch (Exception e)
            {
                _logger.ErrorException("Error registering appstore purchase {0}", e, parameters ?? "NO PARMS SENT");
                SaveAppStoreInfo(parameters);
                //TODO - could create a re-try routine on start-up if this file is there.  For now we can handle manually.
                throw new ApplicationException("Error registering store sale");
            }

        }

        private void SaveAppStoreInfo(string info)
        {
            // Save all transaction information to a file

            try
            {
                File.WriteAllText(Path.Combine(_appPaths.ProgramDataPath, "apptrans-error.txt"), info);
            }
            catch (IOException)
            {

            }
        }

        /// <summary>
        /// Resets the supporter info.
        /// </summary>
        private void ResetSupporterInfo()
        {
            _isMbSupporter = null;
            _isMbSupporterInitialized = false;
        }
    }
}
