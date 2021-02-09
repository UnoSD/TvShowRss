using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Pulumi;
using Pulumi.Azure.AppService;
using Pulumi.Azure.Storage;
using Pulumi.Azure.Storage.Inputs;
using Pulumi.AzureAD;
using Pulumi.AzureNextGen.ApiManagement.Latest;
using Pulumi.AzureNextGen.ApiManagement.V20200601Preview.Inputs;
using Pulumi.AzureNextGen.Insights.Latest;
using Pulumi.AzureNextGen.KeyVault.Latest;
using Pulumi.AzureNextGen.KeyVault.Latest.Inputs;
using Pulumi.AzureNextGen.Resources.Latest;
using Pulumi.AzureNextGen.Storage.Latest;
using Pulumi.AzureNextGen.Storage.Latest.Inputs;
using Pulumi.AzureNextGen.Web.Latest;
using Pulumi.AzureNextGen.Web.Latest.Inputs;
using ApiManagementServiceSkuPropertiesArgs =
    Pulumi.AzureNextGen.ApiManagement.Latest.Inputs.ApiManagementServiceSkuPropertiesArgs;
using BackendCredentialsContractArgs = Pulumi.AzureNextGen.ApiManagement.Latest.Inputs.BackendCredentialsContractArgs;
using Config = Pulumi.Config;
using Deployment = Pulumi.Deployment;
using ManagedServiceIdentityType = Pulumi.AzureNextGen.Web.Latest.ManagedServiceIdentityType;
using SkuName = Pulumi.AzureNextGen.KeyVault.Latest.SkuName;
using Table = Pulumi.AzureNextGen.Storage.Latest.Table;
using TableArgs = Pulumi.AzureNextGen.Storage.Latest.TableArgs;
using NamedValue = Pulumi.AzureNextGen.ApiManagement.V20200601Preview.NamedValue;
using NamedValueArgs = Pulumi.AzureNextGen.ApiManagement.V20200601Preview.NamedValueArgs;
using ApiManagementServiceIdentityArgs =
    Pulumi.AzureNextGen.ApiManagement.Latest.Inputs.ApiManagementServiceIdentityArgs;
using GetClientConfig = Pulumi.AzureNextGen.Authorization.Latest.GetClientConfig;
using GetClientConfigResult = Pulumi.AzureNextGen.Authorization.Latest.GetClientConfigResult;

// Backlog
// Add Git commit to the infrastructure tags to trace back to the deployed version
// Change all "traktClientId" to use a constant and then build + secret etc...
// Storage account network ACL to limit to function outbound IPs

namespace TvShowRss
{
    [SuppressMessage("ReSharper", "ObjectCreationAsStatement")]
    static class Program
    {
        const string TraktClientId = nameof(TraktClientId);
        const string TraktClientSecret = nameof(TraktClientSecret);
        const string TableConnectionString = nameof(TableConnectionString);
        const string TmdbApiKey = nameof(TmdbApiKey);
        const string AppInsightKey = nameof(AppInsightKey);

        const string AppPath = "../Application/bin/publish";

        static readonly Lazy<StackReference> LazyStack =
            new Lazy<StackReference>(() => new StackReference(Deployment.Instance.StackName));

        static StackReference Stack => LazyStack.Value;

        const string SecretsUris = nameof(SecretsUris);
        const string ApplicationMd5 = nameof(ApplicationMd5);
        const string FunctionIdentity = nameof(FunctionIdentity);
        const string ApimIdentity = nameof(ApimIdentity);
        const string GetFeedFunctionName = "GetFeed";
        const string AddShowFunctionName = "AddShow";

        static Task<int> Main() => Deployment.RunAsync(async () =>
        {
            WaitForDebuggerIfRequested();

            var config = new Config("azure");
            var resourcesPrefix = config.Require("resourcesPrefix");
            var location = config.Require("location");
            var azureConfig = await GetClientConfig.InvokeAsync();

            var resourceGroup = CreateResourceGroup(config, location);

            var storageAccount = CreateStorage(resourcesPrefix, resourceGroup);

            CreateSeriesTable(resourceGroup, storageAccount);

            var appInsights = CreateAppInsights(resourceGroup, resourcesPrefix);

            var deploymentsContainer = CreateDeploymentsBlobContainer(storageAccount, resourceGroup);

            var appPackage = CreateAppPackageBlob(storageAccount, deploymentsContainer);

            var appServicePlan = CreateAppServicePlan(location, resourceGroup, config, resourcesPrefix);

            var storageConnectionString = GetStorageConnectionString(resourceGroup, storageAccount);

            var blobUrl = GetAppPackageBlobUrl(deploymentsContainer, appPackage.Url, storageConnectionString);

            var previousMd5 = await Stack.GetStringAsync(nameof(ApplicationMd5));

            var secretUris =
                (await Stack.GetAsync<ImmutableDictionary<string, object>>(nameof(SecretsUris)))?.CastValue<string>();

            var appSourceMd5 = GetAppSourceMd5(AppPath);

            var functionApp =
                FunctionApp(resourcesPrefix,
                            location,
                            resourceGroup,
                            appServicePlan.Id,
                            blobUrl,
                            appSourceMd5 == previousMd5,
                            key => GetKeyVaultReference(secretUris, key));

            var appSecrets = KeyVault(resourceGroup,
                                      azureConfig,
                                      functionApp,
                                      resourcesPrefix,
                                      // Workaround for a Pulumi issue that forgets the function identity
                                      await Stack.GetStringAsync(FunctionIdentity),
                                      await Stack.GetStringAsync(ApimIdentity));

            var traktIdSecret = SecretFromConfig(resourceGroup, appSecrets, config, TraktClientId.ToCamelCase());

            var traktSecretSecret =
                SecretFromConfig(resourceGroup, appSecrets, config, TraktClientSecret.ToCamelCase());

            var tableConnectionStringSecret =
                Secret(resourceGroup, appSecrets, storageConnectionString, TableConnectionString.ToCamelCase());

            var tmdbApiKeySecret = SecretFromConfig(resourceGroup, appSecrets, config, TmdbApiKey.ToCamelCase());

            var appInsightKeySecret =
                Secret(resourceGroup, appSecrets, appInsights.InstrumentationKey, AppInsightKey.ToCamelCase());

            var defaultHostKey = GetDefaultHostKey(functionApp);

            var functionKeySecret = Secret(resourceGroup, appSecrets, defaultHostKey, "functionKey");

            var apiManagement = ApiManagement(resourcesPrefix, resourceGroup);

            var apimIdentityClientId = GetMiApplicationId(apiManagement);

            var faKeyNamedValue =
                FaKeyNamedValue(apiManagement, resourceGroup, functionKeySecret, apimIdentityClientId);

            var backend = FunctionAppApimBackend(functionApp, resourceGroup, apiManagement, faKeyNamedValue);

            var api = ApimApi(apiManagement, resourceGroup);

            AllOperationsPolicy(api, apiManagement, resourceGroup, backend);

            var getFeed = CreateApiOperation(GetFeedFunctionName, api, resourceGroup, apiManagement, HttpMethod.Get);

            var addShow = CreateApiOperation(AddShowFunctionName, api, resourceGroup, apiManagement, HttpMethod.Post);

            var apimSubscription = ApimSubscription(resourceGroup, apiManagement, api);

            var isSecondRun = GetIsSecondRun(secretUris);

            LogRunInstructions(isSecondRun);

            var getFeedUrl = GetUrl(apiManagement, api, getFeed.UrlTemplate, apimSubscription);

            return new Dictionary<string, object?>
            {
                [SecretsUris] = new Dictionary<string, Output<string>>
                {
                    [TraktClientId]         = traktIdSecret.UriWithVersion(),
                    [TraktClientSecret]     = traktSecretSecret.UriWithVersion(),
                    [TableConnectionString] = tableConnectionStringSecret.UriWithVersion(),
                    [TmdbApiKey]            = tmdbApiKeySecret.UriWithVersion(),
                    [AppInsightKey]         = appInsightKeySecret.UriWithVersion()
                },
                [ApplicationMd5]        = Output.Create(appSourceMd5),
                [FunctionIdentity]      = GetFunctionIdentity(functionApp),
                [ApimIdentity]          = apiManagement.Identity.Apply(x => x?.PrincipalId),
                ["FunctionTestResult"]  = TestFunctionInvocation(functionApp, defaultHostKey, isSecondRun),
                ["ApimTestResult"]      = TestUrl(getFeedUrl, isSecondRun),
                ["FunctionOutboundIPs"] = functionApp.OutboundIpAddresses,
                ["GetFeedURL"]          = getFeedUrl,
                ["AddShowURL"]          = GetAddShowUrl(apiManagement, api, addShow, apimSubscription)
            };
        });

        static Output<string> GetAddShowUrl(
            ApiManagementService apiManagement,
            Api api,
            ApiOperation addShow,
            Subscription apimSubscription) =>
            GetUrl(apiManagement, api, addShow.UrlTemplate, apimSubscription)
               .Apply(x => x + "&id=<Trakt TV show ID E.G. friends>");

        static Output<string> GetFunctionIdentity(WebApp functionApp) =>
            functionApp.Identity.Apply(async x => x?.PrincipalId ??
                                                  await Stack.GetStringAsync(nameof(FunctionIdentity)) ??
                                                  string.Empty);

        static void LogRunInstructions(bool isSecondRun)
        {
            if (isSecondRun)
                Log.Info("Second run completed successfully, the stack is now ready");
            else
                Log.Warn("Due to circular dependencies, the stack is not ready, a second *pulumi up* is required after");
        }

        static bool GetIsSecondRun(ImmutableDictionary<string, string>? secretUris) =>
            secretUris != null &&
            secretUris.All(kvp => !string.IsNullOrWhiteSpace(kvp.Key));

        static Output<string> GetUrl(
            ApiManagementService apiManagement,
            Api api,
            Output<string> urlTemplate,
            Subscription apimSubscription) =>
            Output.Format($"{apiManagement.GatewayUrl}/{api.Path}{urlTemplate}?subscription-key={apimSubscription.PrimaryKey}");

        static Output<string> UriWithVersion(this Secret secret) =>
            secret.Properties.Apply(spr => spr.SecretUriWithVersion);

        static string GetKeyVaultReference(ImmutableDictionary<string, string>? secretUris, string secretName) =>
            $"@Microsoft.KeyVault(SecretUri={GetSecretUri(secretUris, secretName)})";

        static ImmutableDictionary<string, T> CastValue<T>(this ImmutableDictionary<string, object> dict) =>
            ImmutableDictionary.CreateRange(dict.Select(kvp => new KeyValuePair<string, T>(kvp.Key, (T) kvp.Value)));

        static Task<string?> GetStringAsync(this StackReference stack, string key) =>
            stack.GetAsync<string>(key);

        static async Task<T?> GetAsync<T>(this StackReference stack, string key) where T : class =>
            (T?) await stack.GetValueAsync(key);

        static void CreateSeriesTable(ResourceGroup resourceGroup, StorageAccount mainStorage) =>
            new Table("storageSeriesTable",
                      new TableArgs
                      {
                          ResourceGroupName = resourceGroup.Name,
                          AccountName       = mainStorage.Name,
                          TableName         = "series"
                      });

        static Subscription ApimSubscription(
            ResourceGroup resourceGroup,
            ApiManagementService apiManagement,
            Api api) =>
            new Subscription("apimFunctionSubscription",
                             new SubscriptionArgs
                             {
                                 ResourceGroupName = resourceGroup.Name,
                                 ServiceName       = apiManagement.Name,
                                 Sid               = "tvshow",
                                 DisplayName       = "API tvshow subscription",
                                 Scope             = Output.Format($"/apis/{api.Name}")
                             });

        static Backend FunctionAppApimBackend(
            WebApp functionApp,
            ResourceGroup resourceGroup,
            ApiManagementService apiManagement,
            NamedValue faKeyNamedValue) =>
            new Backend("apiManagementFunctionBackend",
                        new BackendArgs
                        {
                            BackendId         = functionApp.Name,
                            ResourceGroupName = resourceGroup.Name,
                            ServiceName       = apiManagement.Name,
                            Protocol          = BackendProtocol.Http,
                            Url               = Output.Format($"https://{functionApp.DefaultHostName}/api"),
                            Credentials = new BackendCredentialsContractArgs
                            {
                                Header =
                                    faKeyNamedValue.Name.Apply(n =>
                                                                   new Dictionary<string,
                                                                       ImmutableArray<string>>
                                                                   {
                                                                       ["x-functions-key"] =
                                                                           new[] {$"{{{{{n}}}}}"}
                                                                              .ToImmutableArray()
                                                                   })
                            }
                        });

        static NamedValue FaKeyNamedValue(
            ApiManagementService apiManagement,
            ResourceGroup resourceGroup,
            Secret functionKeySecret,
            Output<string> apimIdentityClientId) =>
            new NamedValue("apiManagementNamedValueKeyVaultFunctionKey",
                           new NamedValueArgs
                           {
                               Secret            = true,
                               ServiceName       = apiManagement.Name,
                               ResourceGroupName = resourceGroup.Name,
                               DisplayName       = "FunctionKey",
                               NamedValueId      = "function-key",
                               KeyVault = new KeyVaultContractCreatePropertiesArgs
                               {
                                   SecretIdentifier =
                                       functionKeySecret.Properties
                                                        .Apply(x => x.SecretUriWithVersion),
                                   IdentityClientId = apimIdentityClientId
                               }
                           });

        static Output<string> GetMiApplicationId(ApiManagementService apiManagement) =>
            apiManagement.Identity
                         .Apply(x => x?.PrincipalId ?? throw new Exception("Missing APIM identity"))
                         .Apply(x => GetServicePrincipal.InvokeAsync(new GetServicePrincipalArgs
                          {
                              ObjectId = x
                          }))
                         .Apply(x => x.ApplicationId);

        static ApiOperation CreateApiOperation(
            string operationName,
            Api api,
            ResourceGroup resourceGroup,
            ApiManagementService apim,
            HttpMethod httpMethod) =>
            new ApiOperation(ToCamelCase(operationName) + "Operation",
                             new ApiOperationArgs
                             {
                                 ApiId             = api.Name,
                                 OperationId       = operationName,
                                 ResourceGroupName = resourceGroup.Name,
                                 ServiceName       = apim.Name,
                                 DisplayName       = operationName,
                                 Method            = httpMethod.Method,
                                 UrlTemplate       = "/" + operationName
                             });

        static string ToCamelCase(this string value) =>
            $"{char.ToLower(value[0])}{value.Substring(1)}";

        static void AllOperationsPolicy(Api api, ApiManagementService apim, ResourceGroup resourceGroup, Backend be) =>
            new ApiPolicy("apiPolicy",
                          new ApiPolicyArgs
                          {
                              ApiId             = api.Name,
                              PolicyId          = "policy",
                              ResourceGroupName = resourceGroup.Name,
                              ServiceName       = apim.Name,
                              Value = Output.Format($@"
<policies>
    <inbound>
        <rate-limit calls=""8"" renewal-period=""300"" />
        <set-backend-service id=""apim-generated-policy"" backend-id=""{be.Name}"" />
        <base />
    </inbound>
    <backend>
        <base />
    </backend>
    <outbound>
        <base />
    </outbound>
    <on-error>
        <base />
    </on-error>
</policies>"),
                              Format = PolicyContentFormat.Rawxml
                          });

        static Api ApimApi(ApiManagementService apiManagement, ResourceGroup resourceGroup) =>
            new Api("api",
                    new ApiArgs
                    {
                        ServiceName       = apiManagement.Name,
                        DisplayName       = "tvshowrss",
                        ApiId             = "tvshowsrss",
                        ResourceGroupName = resourceGroup.Name,
                        Path              = "tvshowrss",
                        Protocols         = {Protocol.Https}
                    });

        static ApiManagementService ApiManagement(
            string resourcesPrefix,
            ResourceGroup resourceGroup) =>
            new ApiManagementService("apiManagement",
                                     new ApiManagementServiceArgs
                                     {
                                         ResourceGroupName = resourceGroup.Name,
                                         Location          = resourceGroup.Location,
                                         ServiceName       = resourcesPrefix + "apim",
                                         PublisherEmail    = "unosd@apimanagement.unosd",
                                         PublisherName     = "UnoSD",
                                         Identity = new ApiManagementServiceIdentityArgs
                                         {
                                             Type = ApimIdentityType.SystemAssigned
                                         },
                                         Sku = new ApiManagementServiceSkuPropertiesArgs
                                         {
                                             Name     = SkuType.Consumption,
                                             Capacity = 0
                                         }
                                     });

        static Output<string> TestFunctionInvocation(WebApp functionApp, Output<string> key, bool isSecondRun) =>
            TestUrl(Output
                       .Format($"https://{functionApp.DefaultHostName}/api/{GetFeedFunctionName}?code={key}"),
                    isSecondRun);

        static Output<string> TestUrl(Output<string> url, bool isSecondRun) =>
            isSecondRun ?
                url.Apply(x => new HttpClient().GetAsync(x))
                   .Apply(async response => response.IsSuccessStatusCode ?
                              "Successful" :
                              $"Failed with: {response.StatusCode} {await response.Content.ReadAsStringAsync()}") :
                Output.Create("Run the deployment again to set app settings to correct Key Vault references");

        static Output<string> GetDefaultHostKey(WebApp functionApp) =>
            Output.Tuple(functionApp.Name, functionApp.ResourceGroup)
                  .Apply(tuple => GetFunctionAppHostKeys.InvokeAsync(new GetFunctionAppHostKeysArgs
                   {
                       Name              = tuple.Item1,
                       ResourceGroupName = tuple.Item2
                   }))
                  .Apply(r => r.DefaultFunctionKey);

        /// <summary>
        /// Enable with: pulumi &lt;up/preview&gt; -c waitForDebugger=true; sed -i 's/^  debug:waitForDebugger: "true"$//' Pulumi.&lt;stackname&gt;.yaml
        /// </summary>
        static void WaitForDebuggerIfRequested()
        {
            var waitForDebugger = new Config("debug").GetBoolean("waitForDebugger") ?? false;

            if (waitForDebugger)
                Log.Warn($"PID: {Process.GetCurrentProcess().Id} Waiting for .NET debugger to be attached...");

            while (waitForDebugger && !Debugger.IsAttached)
                System.Threading.Thread.Sleep(500);
        }

        static string GetAppSourceMd5(string appPath)
        {
            IEnumerable<Action<ICryptoTransform>> AddBlocks(string file, bool isLast)
            {
                var path = Encoding.UTF8.GetBytes(file.Substring(appPath.Length + 1));

                yield return ct => ct.TransformBlock(path, 0, path.Length, path, 0);

                var content = File.ReadAllBytes(file);

                yield return isLast ?
                    (Action<ICryptoTransform>) (ct => ct.TransformFinalBlock(content, 0, content.Length)) :
                    ct => ct.TransformBlock(content, 0, content.Length, content, 0);
            }

            var allFiles =
                Directory.EnumerateFiles(appPath, "*", SearchOption.AllDirectories)
                         .OrderBy(fn => fn)
                         .ToList();

            var actions =
                allFiles.SelectMany((file, index) => AddBlocks(file, index == allFiles.Count - 1));

            using var md5 = MD5.Create();

            foreach (var action in actions)
                action(md5);

            return BitConverter.ToString(md5.Hash);
        }

        static Output<string> GetAppPackageBlobUrl(
            BlobContainer deploymentsContainer,
            Output<string> appPackageUrl,
            Output<string> storageConnectionString) =>
            Output.Tuple(deploymentsContainer.Name, storageConnectionString)
                  .Apply(tuple =>
                             GetAccountBlobContainerSAS.InvokeAsync(new GetAccountBlobContainerSASArgs
                             {
                                 Start = DateTime.UtcNow
                                                 .ToString("O", CultureInfo.InvariantCulture),
                                 Expiry = DateTime.UtcNow
                                                  .AddYears(1)
                                                  .ToString("O", CultureInfo.InvariantCulture),
                                 // Find an alternative that works for good, don't want to update the token every year
                                 ContainerName    = tuple.Item1,
                                 HttpsOnly        = true,
                                 ConnectionString = tuple.Item2,
                                 Permissions = new GetAccountBlobContainerSASPermissionsArgs
                                 {
                                     Read = true
                                 }
                             }))
                  .Apply(x => appPackageUrl.Apply(url => url + x.Sas));

        static Blob CreateAppPackageBlob(StorageAccount mainStorage, BlobContainer deploymentsCntainer)
        {
            var startInfo =
                new ProcessStartInfo("dotnet",
                                     "publish -r linux-x64 -o ../Application/bin/publish ../Application/TvShowRss.csproj")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };

            var process =
                Process.Start(startInfo)!;

            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception("Application compilation error");

            return new Blob("appPackage",
                            new BlobArgs
                            {
                                AccessTier           = "Hot",
                                Source               = new FileArchive(AppPath),
                                StorageAccountName   = mainStorage.Name,
                                StorageContainerName = deploymentsCntainer.Name,
                                Type                 = "Block",
                                Name                 = "application.zip"
                            });
        }

        static Secret SecretFromConfig(
            ResourceGroup resourceGroup,
            Vault appSecrets,
            Config config,
            string configKey) =>
            Secret(resourceGroup, appSecrets, config.RequireSecret(configKey), configKey);

        static Secret Secret(ResourceGroup resourceGroup, Vault appSecrets, Output<string> value, string name) =>
            new Secret(name + "Secret",
                       new SecretArgs
                       {
                           Properties = new SecretPropertiesArgs
                           {
                               Attributes = new SecretAttributesArgs
                               {
                                   Enabled = true
                               },
                               Value = value
                           },
                           ResourceGroupName = resourceGroup.Name,
                           SecretName        = name.ToPascalCase(),
                           VaultName         = appSecrets.Name
                       });

        static string ToPascalCase(this string value) =>
            $"{char.ToUpper(value[0])}{value.Substring(1)}";

        static Vault KeyVault(
            ResourceGroup resourceGroup,
            GetClientConfigResult azureConfig,
            WebApp functionApp,
            string resourcesPrefix,
            string? savedIdentity,
            string? apimIdentity) =>
            new Vault("appSecrets",
                      new VaultArgs
                      {
                          Location = resourceGroup.Location,
                          Properties = new VaultPropertiesArgs
                          {
                              EnableRbacAuthorization = false,
                              EnableSoftDelete        = false,
                              AccessPolicies =
                                  new[]
                                      {
                                          new AccessPolicyEntryArgs
                                          {
                                              ObjectId = Output.Create(azureConfig.ObjectId),
                                              Permissions = new PermissionsArgs
                                              {
                                                  Secrets =
                                                  {
                                                      "get",
                                                      "set",
                                                      "list",
                                                      "delete"
                                                  }
                                              },
                                              TenantId = azureConfig.TenantId
                                          },
                                          new AccessPolicyEntryArgs
                                          {
                                              ObjectId =
                                                  functionApp.Identity.Apply(x => x?.PrincipalId ??
                                                                                  savedIdentity ??
                                                                                  //throw new Exception("Missing function identity")),
                                                                                  "Missing function identity, file a bug in Pulumi"),
                                              Permissions = new PermissionsArgs
                                              {
                                                  Secrets =
                                                  {
                                                      "get",
                                                      "set",
                                                      "list"
                                                  }
                                              },
                                              TenantId = azureConfig.TenantId
                                          }
                                      }.Concat(apimIdentity is null ?
                                                   Array.Empty<AccessPolicyEntryArgs>() :
                                                   new[]
                                                   {
                                                       new AccessPolicyEntryArgs
                                                       {
                                                           ObjectId = apimIdentity,
                                                           Permissions = new PermissionsArgs
                                                           {
                                                               Secrets =
                                                               {
                                                                   "get",
                                                                   "list"
                                                               }
                                                           },
                                                           TenantId = azureConfig.TenantId
                                                       }
                                                   })
                                       .ToList(),
                              EnabledForDeployment         = false,
                              EnabledForDiskEncryption     = false,
                              EnabledForTemplateDeployment = false,
                              ProvisioningState            = "Succeeded",
                              Sku = new Pulumi.AzureNextGen.KeyVault.Latest.Inputs.SkuArgs
                              {
                                  Family = "A",
                                  Name   = SkuName.Standard
                              },
                              TenantId = azureConfig.TenantId,
                              VaultUri = $"https://{resourcesPrefix + "kv"}.vault.azure.net/"
                          },
                          ResourceGroupName = resourceGroup.Name,
                          VaultName         = resourcesPrefix + "kv"
                      });

        static BlobContainer CreateDeploymentsBlobContainer(StorageAccount mainStorage, ResourceGroup resourceGroup) =>
            new BlobContainer("deploymentsContainer",
                              new BlobContainerArgs
                              {
                                  AccountName                 = mainStorage.Name,
                                  ContainerName               = "deployments",
                                  DefaultEncryptionScope      = "$account-encryption-key",
                                  DenyEncryptionScopeOverride = false,
                                  PublicAccess                = PublicAccess.None,
                                  ResourceGroupName           = resourceGroup.Name
                              });

        static WebApp FunctionApp(
            string resourcesPrefix,
            string location,
            ResourceGroup resourceGroup,
            Output<string> appServicePlanId,
            Output<string> appPackageBlobUrl,
            bool md5Unchanged,
            Func<string, string> getKeyVaultReference) =>
            new WebApp("functionApp",
                       new WebAppArgs
                       {
                           ClientAffinityEnabled = false,
                           HttpsOnly             = true,
                           Identity = new ManagedServiceIdentityArgs
                           {
                               Type = ManagedServiceIdentityType.SystemAssigned
                           },
                           Kind              = "functionapp,linux",
                           Location          = location,
                           Name              = resourcesPrefix + "fa",
                           RedundancyMode    = RedundancyMode.None,
                           Reserved          = true,
                           ResourceGroupName = resourceGroup.Name,
                           ServerFarmId      = appServicePlanId.Apply(x => x.Replace("serverFarms", "serverfarms")),
                           SiteConfig = new SiteConfigArgs
                           {
                               LinuxFxVersion = "dotnet|3.1",
                               AppSettings = AppSettings(appPackageBlobUrl, getKeyVaultReference)
                                            .Select(kvp => new NameValuePairArgs {Name = kvp.Key, Value = kvp.Value})
                                            .ToList()
                           }
                       },
                       new CustomResourceOptions
                       {
                           IgnoreChanges = md5Unchanged ?
                               new List<string>
                               {
                                   // This is why WEBSITE_RUN_FROM_PACKAGE must stay at first position
                                   "siteConfig.appSettings[0].value"
                               } :
                               new List<string>()
                       });

        static Dictionary<string, Input<string>> AppSettings(
            Output<string> appPackageBlobUrl,
            Func<string, string> getKeyVaultReference) =>
            new Dictionary<string, Input<string>>
            {
                // WEBSITE_RUN_FROM_PACKAGE must stay on top to be ignored if MD5 unchanged
                ["WEBSITE_RUN_FROM_PACKAGE"] = appPackageBlobUrl,

                ["FUNCTIONS_WORKER_RUNTIME"]       = "dotnet",
                ["FUNCTION_APP_EDIT_MODE"]         = "readwrite",
                ["APPINSIGHTS_INSTRUMENTATIONKEY"] = getKeyVaultReference(AppInsightKey),
                ["AzureWebJobsStorage"]            = getKeyVaultReference(TableConnectionString),
                [TableConnectionString]            = getKeyVaultReference(TableConnectionString),
                [TraktClientId]                    = getKeyVaultReference(TraktClientId),
                [TraktClientSecret]                = getKeyVaultReference(TraktClientSecret),
                [TmdbApiKey]                       = getKeyVaultReference(TmdbApiKey),
                ["CheckDays"]                      = "5",
                ["FUNCTIONS_EXTENSION_VERSION"]    = "~3"
            };

        static string GetSecretUri(ImmutableDictionary<string, string>? secretUris, string outputName) =>
            !(secretUris is null) && secretUris.TryGetValue(outputName, out var value) ? value : string.Empty;

        static Output<string> GetStorageConnectionString(ResourceGroup resourceGroup, StorageAccount mainStorage) =>
            Output.Tuple(mainStorage.Name, resourceGroup.Name)
                  .Apply(async tuple => (
                             result: await ListStorageAccountKeys.InvokeAsync(new ListStorageAccountKeysArgs
                             {
                                 AccountName       = tuple.Item1,
                                 ResourceGroupName = tuple.Item2
                             }), accountName: tuple.Item1))
                  .Apply(tuple => $"DefaultEndpointsProtocol=https;AccountName={tuple.accountName};" +
                                  $"AccountKey={tuple.result.Keys.First().Value}");
        // When added, this causes the appSettings of the function app to turn secret and that
        // somehow breaks the update when ignoring the changes to the WEBSITE_RUN_FROM_PACKAGE
        // File an issue on pulumi-azure-nextgen on GitHub
        //.Apply(Output.CreateSecret);

        static AppServicePlan CreateAppServicePlan(
            string location,
            ResourceGroup resourceGroup,
            Config conf,
            string resourcePrefix) =>
            new AppServicePlan("appServicePlan",
                               new AppServicePlanArgs
                               {
                                   HyperV                    = false,
                                   IsSpot                    = false,
                                   IsXenon                   = false,
                                   Kind                      = "functionapp",
                                   Location                  = location,
                                   MaximumElasticWorkerCount = 1,
                                   Name = GetResourceName(conf,
                                                          resourcePrefix + "asp",
                                                          "appServicePlan"),
                                   PerSiteScaling    = false,
                                   Reserved          = true,
                                   ResourceGroupName = resourceGroup.Name,
                                   Sku = new SkuDescriptionArgs
                                   {
                                       Capacity = 0,
                                       Family   = "Y",
                                       Name     = "Y1",
                                       Size     = "Y1",
                                       Tier     = "Dynamic"
                                   },
                                   TargetWorkerCount  = 0,
                                   TargetWorkerSizeId = 0
                               });

        static string GetResourceName(Config config, string defaultName, string pulumiName)
        {
            var nameOverrides =
                config.GetObject<ImmutableDictionary<string, string>>("nameOverride");

            return nameOverrides is null ?
                defaultName :
                nameOverrides.TryGetValue(pulumiName, out var value) ?
                    value :
                    defaultName;
        }

        static Component CreateAppInsights(ResourceGroup resourceGroup, string resourcesPrefix) =>
            new Component("appInsights",
                          new ComponentArgs
                          {
                              ApplicationType   = "web",
                              Kind              = "web",
                              Location          = resourceGroup.Location,
                              ResourceGroupName = resourceGroup.Name,
                              ResourceName      = resourcesPrefix + "ai",
                              RetentionInDays   = 90
                          });

        static StorageAccount CreateStorage(string resourcesPrefix, ResourceGroup resourceGroup) =>
            new StorageAccount("storageAccount",
                               new StorageAccountArgs
                               {
                                   AccessTier             = AccessTier.Hot,
                                   AccountName            = resourcesPrefix + "sa",
                                   EnableHttpsTrafficOnly = false,
                                   Encryption = new EncryptionArgs
                                   {
                                       KeySource = "Microsoft.Storage",
                                       Services = new EncryptionServicesArgs
                                       {
                                           Blob = new EncryptionServiceArgs
                                           {
                                               Enabled = true,
                                               KeyType = "Account"
                                           },
                                           File = new EncryptionServiceArgs
                                           {
                                               Enabled = true,
                                               KeyType = "Account"
                                           }
                                       }
                                   },
                                   IsHnsEnabled = false,
                                   Kind         = "StorageV2",
                                   Location     = resourceGroup.Location,
                                   NetworkRuleSet = new Pulumi.AzureNextGen.Storage.Latest.Inputs.NetworkRuleSetArgs
                                   {
                                       Bypass        = "AzureServices",
                                       DefaultAction = DefaultAction.Allow
                                   },
                                   ResourceGroupName = resourceGroup.Name,
                                   Sku = new Pulumi.AzureNextGen.Storage.Latest.Inputs.SkuArgs
                                   {
                                       Name = "Standard_LRS"
                                   }
                               });

        static ResourceGroup CreateResourceGroup(Config config, string location) =>
            new ResourceGroup("resourceGroup",
                              new ResourceGroupArgs
                              {
                                  ResourceGroupName = config.Require("resourceGroup"),
                                  Location          = location.ToLower().Replace(" ", "")
                              });
    }
}