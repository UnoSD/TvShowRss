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
using Action = System.Action;
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

namespace TvShowRss
{
    [SuppressMessage("ReSharper", "ObjectCreationAsStatement")]
    static class Program
    {
        const string TraktIdSecretOutputName = "TraktIdSecret";
        const string TraktSecretSecretOutputName = "TraktSecretSecret";
        const string TableConnectionStringSecretOutputName = "TableConnectionStringSecret";
        const string TmdbApiKeySecretOutputName = "TmdbApiKeySecret";

        const string AppPath = "../Application/bin/publish";

        // This stack needs to be deployed twice, the second time, it will set the Key Vault
        // secret URIs in the app settings.

        // Debug show REST API calls: pulumi up -v=9 --logtostderr --logflow --debug

        [SuppressMessage("ReSharper", "ObjectCreationAsStatement")]
        static Task<int> Main() => Deployment.RunAsync(async () =>
        {
            WaitForDebuggerIfRequested();

            var config = new Config("azure");
            var resourcesPrefix = config.Require("resourcesPrefix");
            var location = config.Require("location");
            var azureConfig = await GetClientConfig.InvokeAsync();

            var resourceGroup = ResourceGroup(config, location);

            var mainStorage = MainStorage(resourcesPrefix, resourceGroup);

            new Table("storageSeriesTable",
                      new TableArgs
                      {
                          ResourceGroupName = resourceGroup.Name,
                          AccountName       = mainStorage.Name,
                          TableName         = "series"
                      });

            var appInsights = AppInsights(resourceGroup, resourcesPrefix);

            var deploymentsContainer = BlobContainer(mainStorage, resourceGroup);

            var appPackage = AppPackage(mainStorage, deploymentsContainer);

            var appServicePlan = AppServicePlan(location, resourceGroup, config, resourcesPrefix);

            var storageConnectionString = GetStorageConnectionString(resourceGroup, mainStorage);

            var blobUrl = GetAppPackageBlobUrl(deploymentsContainer, appPackage.Url, storageConnectionString);

            var stack =
                new StackReference(Deployment.Instance.StackName);

            var previousMd5 =
                (string?) await stack.GetValueAsync(nameof(ApplicationMd5));

            var secretUris =
                ((ImmutableDictionary<string, object>?) await stack.GetValueAsync(nameof(SecretsUris)))?
               .ToImmutableDictionary(kvp => kvp.Key, kvp => (string) kvp.Value);

            // Could also use the Git commit, but won't work during dev
            var appSourceMd5 = GetAppSourceMd5(AppPath);

            string KeyVaultReference(string secretName) =>
                $"@Microsoft.KeyVault(SecretUri={GetSecretUri(secretUris, secretName)})";

            var functionApp =
                FunctionApp(config,
                            resourcesPrefix,
                            location,
                            resourceGroup,
                            appServicePlan.Id,
                            appInsights,
                            blobUrl,
                            appSourceMd5 == previousMd5,
                            KeyVaultReference);

            // Workaround for a Pulumi issue that forgets the function identity
            var savedIdentity = (await stack.GetValueAsync(nameof(FunctionIdentity)))?.ToString();

            var appSecrets = KeyVault(resourceGroup,
                                      config,
                                      azureConfig,
                                      functionApp,
                                      resourcesPrefix,
                                      savedIdentity,
                                      (string?) await stack.GetValueAsync("ApimIdentity"));

            var traktIdSecret = SecretFromConfig(resourceGroup, appSecrets, config, "traktClientId");

            var traktSecretSecret = SecretFromConfig(resourceGroup, appSecrets, config, "traktClientSecret");

            var tableConnectionStringSecret =
                Secret(resourceGroup, appSecrets, storageConnectionString, "tableConnectionString");

            var tmdbApiKeySecret = SecretFromConfig(resourceGroup, appSecrets, config, "tmdbApiKey");

            var defaultHostKey = GetDefaultHostKey(functionApp);

            var functionKeySecret = Secret(resourceGroup, appSecrets, defaultHostKey, "FunctionKey");

            var apiManagement = ApiManagement(resourcesPrefix, resourceGroup);

            var apimIdentityClientId = GetMiApplicationId(apiManagement);

            var faKeyNamedValue =
                FaKeyNamedValue(apiManagement, resourceGroup, functionKeySecret, apimIdentityClientId);

            var backend = FunctionAppApimBackend(functionApp, resourceGroup, apiManagement, faKeyNamedValue);

            var api = ApimApi(apiManagement, resourceGroup);

            AllOperationsPolicy(api, apiManagement, resourceGroup, backend);

            CreateApiOperation(GetFeedFunctionName, api, resourceGroup, apiManagement, HttpMethod.Get);

            CreateApiOperation(AddShowFunctionName, api, resourceGroup, apiManagement, HttpMethod.Post);

            var apimSubscription = new Subscription("apimFunctionSubscription",
                                                    new SubscriptionArgs
                                                    {
                                                        ResourceGroupName = resourceGroup.Name,
                                                        ServiceName       = apiManagement.Name,
                                                        Sid               = "tvshow",
                                                        DisplayName       = "API tvshow subscription",
                                                        Scope             = Output.Format($"/apis/{api.Name}")
                                                    });

            static Output<KeyValuePair<string, string>> ToKvp(Secret secret, string key) =>
                secret.Properties.Apply(spr => KeyValuePair.Create(key, spr.SecretUriWithVersion));

            var isSecondRun =
                secretUris != null &&
                secretUris.All(kvp => !string.IsNullOrWhiteSpace(kvp.Key));

            var getFeedUrl =
                Output.Format($"{apiManagement.GatewayUrl}/{api.Path}/{GetFeedFunctionName}?subscription-key={apimSubscription.PrimaryKey}");

            if (isSecondRun)
                Log.Info("Second run completed successfully, the stack is now ready");
            else
                Log.Warn("Due to circular dependencies, the stack is not ready, a second *pulumi up* is required after");

            return new Dictionary<string, object?>
            {
                [ApplicationMd5]   = Output.Create(appSourceMd5),
                [FunctionIdentity] = functionApp.Identity.Apply(x => x?.PrincipalId ?? savedIdentity ?? string.Empty),
                [SecretsUris] =
                    Output.All(ToKvp(traktIdSecret, TraktIdSecretOutputName),
                               ToKvp(traktSecretSecret, TraktSecretSecretOutputName),
                               ToKvp(tableConnectionStringSecret, TableConnectionStringSecretOutputName),
                               ToKvp(tmdbApiKeySecret, TmdbApiKeySecretOutputName))
                          .Apply(kvps => new Dictionary<string, string>(kvps).ToImmutableDictionary()),
                ["FunctionTestResult"]  = TestFunctionInvocation(functionApp, defaultHostKey, isSecondRun),
                ["FunctionOutboundIPs"] = functionApp.OutboundIpAddresses,
                ["ApimIdentity"]        = apiManagement.Identity.Apply(x => x?.PrincipalId),
                ["URL"]                 = getFeedUrl,
                ["ApimTestResult"]      = TestUrl(getFeedUrl, isSecondRun)
            };
        });

        static Backend FunctionAppApimBackend(
            WebApp functionApp,
            ResourceGroup resourceGroup,
            ApiManagementService apiManagement,
            NamedValue faKeyNamedValue)
        {
            return new Backend("apiManagementFunctionBackend",
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
        }

        static NamedValue FaKeyNamedValue(
            ApiManagementService apiManagement,
            ResourceGroup resourceGroup,
            Secret functionKeySecret,
            Output<string> apimIdentityClientId)
        {
            return new NamedValue("apiManagementNamedValueKeyVaultFunctionKey",
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
        }

        static Output<string> GetMiApplicationId(ApiManagementService apiManagement)
        {
            return apiManagement.Identity
                                .Apply(x => x?.PrincipalId ?? throw new Exception("Missing APIM identity"))
                                .Apply(x => GetServicePrincipal.InvokeAsync(new GetServicePrincipalArgs
                                 {
                                     ObjectId = x
                                 }))
                                .Apply(x => x.ApplicationId);
        }

        static void CreateApiOperation(
            string operationName,
            Api api,
            ResourceGroup resourceGroup,
            ApiManagementService apim,
            HttpMethod httpMethod) =>
            new ApiOperation(CamelCase(operationName) + "Operation",
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

        static string CamelCase(string value) =>
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
                        Protocols         = new[] {Protocol.Https}
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

        const string SecretsUris = nameof(SecretsUris);
        const string ApplicationMd5 = nameof(ApplicationMd5);
        const string FunctionIdentity = nameof(FunctionIdentity);
        const string GetFeedFunctionName = "GetFeed";
        const string AddShowFunctionName = "AddShow";

        static Output<string> GetAppPackageBlobUrl(
            BlobContainer deploymentsContainer,
            Output<string> appPackageUrl,
            Output<string> storageConnectionString) =>
            Output.Tuple(deploymentsContainer.Name, storageConnectionString)
                  .Apply(tuple =>
                             GetAccountBlobContainerSAS.InvokeAsync(new GetAccountBlobContainerSASArgs
                             {
                                 Start = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                                 Expiry = DateTime.UtcNow.AddYears(1)
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

        static Blob AppPackage(StorageAccount mainStorage, BlobContainer deploymentsCntainer)
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
                           SecretName        = $"{char.ToUpper(name[0])}{name.Substring(1)}",
                           VaultName         = appSecrets.Name
                       });

        static Vault KeyVault(
            ResourceGroup resourceGroup,
            Config config,
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
                                              ObjectId = config.GetSecret("keyVaultManagerObjectId") ??
                                                         Output.Create(azureConfig.ObjectId),
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

        static BlobContainer BlobContainer(StorageAccount mainStorage, ResourceGroup resourceGroup) =>
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
            Config config,
            string resourcesPrefix,
            string location,
            ResourceGroup resourceGroup,
            Output<string> appServicePlanId,
            Component appInsights,
            Output<string> appPackageBlobUrl,
            bool md5Unchanged,
            Func<string, string> getKeyVaultReference) =>
            new WebApp("functionApp",
                       new WebAppArgs
                       {
                           ClientAffinityEnabled      = false,
                           ClientCertEnabled          = false,
                           ClientCertMode             = ClientCertMode.Required,
                           ContainerSize              = 1536,
                           CustomDomainVerificationId = config.Require("customDomainVerificationId"),
                           DailyMemoryTimeQuota       = 0,
                           Enabled                    = true,
                           HostNameSslStates =
                           {
                               new HostNameSslStateArgs
                               {
                                   HostType = HostType.Standard,
                                   Name     = resourcesPrefix + "fa.azurewebsites.net",
                                   SslState = SslState.Disabled
                               },
                               new HostNameSslStateArgs
                               {
                                   HostType = HostType.Repository,
                                   Name     = resourcesPrefix + "fa.scm.azurewebsites.net",
                                   SslState = SslState.Disabled
                               }
                           },
                           HostNamesDisabled = false,
                           HttpsOnly         = true,
                           HyperV            = false,
                           Identity = new ManagedServiceIdentityArgs
                           {
                               Type = ManagedServiceIdentityType.SystemAssigned
                           },
                           IsXenon            = false,
                           Kind               = "functionapp,linux",
                           Location           = location,
                           Name               = resourcesPrefix + "fa",
                           RedundancyMode     = RedundancyMode.None,
                           Reserved           = true,
                           ResourceGroupName  = resourceGroup.Name,
                           ScmSiteAlsoStopped = false,
                           ServerFarmId       = appServicePlanId.Apply(x => x.Replace("serverFarms", "serverfarms")),
                           SiteConfig = new SiteConfigArgs
                           {
                               LinuxFxVersion = "dotnet|3.1",
                               AppSettings = AppSettings(appInsights, appPackageBlobUrl, getKeyVaultReference)
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
            Component appInsights,
            Output<string> appPackageBlobUrl,
            Func<string, string> getKeyVaultReference) =>
            new Dictionary<string, Input<string>>
            {
                // WEBSITE_RUN_FROM_PACKAGE must stay on top to be ignored if MD5 unchanged
                ["WEBSITE_RUN_FROM_PACKAGE"] = appPackageBlobUrl,

                ["FUNCTIONS_WORKER_RUNTIME"]       = "dotnet",
                ["FUNCTION_APP_EDIT_MODE"]         = "readwrite",
                ["APPINSIGHTS_INSTRUMENTATIONKEY"] = appInsights.InstrumentationKey,
                ["AzureWebJobsStorage"]            = getKeyVaultReference(TableConnectionStringSecretOutputName),
                ["TableConnectionString"]          = getKeyVaultReference(TableConnectionStringSecretOutputName),
                ["TraktClientId"]                  = getKeyVaultReference(TraktIdSecretOutputName),
                ["TraktClientSecret"]              = getKeyVaultReference(TraktSecretSecretOutputName),
                ["TmdbApiKey"]                     = getKeyVaultReference(TmdbApiKeySecretOutputName),
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

        static AppServicePlan AppServicePlan(
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

        static Component AppInsights(ResourceGroup resourceGroup, string resourcesPrefix) =>
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

        static StorageAccount MainStorage(string resourcesPrefix, ResourceGroup resourceGroup) =>
            new StorageAccount("mainStorage",
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

        static ResourceGroup ResourceGroup(Config config, string location) =>
            new ResourceGroup("resourceGroup",
                              new ResourceGroupArgs
                              {
                                  ResourceGroupName = config.Require("resourceGroup"),
                                  Location          = location.ToLower().Replace(" ", "")
                              });
    }
}