using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Pulumi;
using Pulumi.Azure.Storage;
using Pulumi.Azure.Storage.Inputs;
using Pulumi.AzureNextGen.Authorization.Latest;
using Pulumi.AzureNextGen.Insights.Latest;
using Pulumi.AzureNextGen.KeyVault.Latest;
using Pulumi.AzureNextGen.KeyVault.Latest.Inputs;
using Pulumi.AzureNextGen.Resources.Latest;
using Pulumi.AzureNextGen.Storage.Latest;
using Pulumi.AzureNextGen.Storage.Latest.Inputs;
using Pulumi.AzureNextGen.Web.Latest;
using Pulumi.AzureNextGen.Web.Latest.Inputs;
using Config = Pulumi.Config;
using Deployment = Pulumi.Deployment;
using SkuName = Pulumi.AzureNextGen.KeyVault.Latest.SkuName;
using Table = Pulumi.AzureNextGen.Storage.Latest.Table;
using TableArgs = Pulumi.AzureNextGen.Storage.Latest.TableArgs;

namespace TvShowRss
{
    class MyStack : Stack
    {
        const string TraktIdSecretOutputName = "TraktIdSecret";
        const string TraktSecretSecretOutputName = "TraktSecretSecret";
        const string TableConnectionStringSecretOutputName = "TableConnectionStringSecret";
        const string TmdbApiKeySecretOutputName = "TmdbApiKeySecret";

        const string AppPath = "../Application/bin/publish";

        // This stack needs to be deployed twice, the second time, it will set the Key Vault
        // secret URIs in the app settings.

        [SuppressMessage("ReSharper", "ObjectCreationAsStatement")]
        public MyStack()
        {
            //while(!Debugger.IsAttached)
            //    System.Threading.Thread.Sleep(500);

            var config = new Config("azure");
            var resourcesPrefix = config.Require("resourcesPrefix");
            var location = config.Require("location");
            var azureConfig = GetClientConfig.InvokeAsync().Result;

            var resourceGroup = ResourceGroup(config, location);

            var mainStorage = MainStorage(resourcesPrefix, resourceGroup);

            new Table("storageSeriesTable", new TableArgs
            {
                ResourceGroupName = resourceGroup.Name,
                AccountName       = mainStorage.Name,
                TableName         = "series"
            });

            var appInsights = AppInsights(resourceGroup, resourcesPrefix);

            var deploymentsContainer = BlobContainer(mainStorage, resourceGroup);

            var appPackage = AppPackage(mainStorage, deploymentsContainer);

            var appServicePlan = AppServicePlan(location, resourceGroup);

            var storageConnectionString = GetStorageConnectionString(resourceGroup, mainStorage);

            var blobUrl = GetAppPackageBlobUrl(deploymentsContainer, appPackage.Url, storageConnectionString);

            var stack =
                    new StackReference(Deployment.Instance.StackName);

            var previousMd5 =
                    (string?) stack.GetValueAsync(nameof(ApplicationMd5)).Result;

            var secretUris =
                    ((ImmutableDictionary<string, object>?) stack.GetValueAsync(nameof(SecretsUris)).Result)?
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
            var savedIdentity = stack.GetValueAsync(nameof(FunctionIdentity)).Result?.ToString();

            var appSecrets = KeyVault(resourceGroup,
                                      config,
                                      azureConfig,
                                      functionApp,
                                      resourcesPrefix,
                                      savedIdentity);

            var traktIdSecret = TraktIdSecret(resourceGroup, appSecrets, config);

            var traktSecretSecret = TraktSecretSecret(resourceGroup, appSecrets, config);

            var tableConnectionStringSecret =
                    TableConnectionStringSecret(resourceGroup, appSecrets, storageConnectionString);

            var tmdbApiKeySecret = TmdbApiKeySecret(resourceGroup, appSecrets, config);

            ApplicationMd5 = Output.Create(appSourceMd5);

            FunctionIdentity = functionApp.Identity.Apply(x => x?.PrincipalId ?? savedIdentity ?? string.Empty);

            SecretsUris =
                    Output.Tuple(traktIdSecret.Properties,
                                 traktSecretSecret.Properties,
                                 tableConnectionStringSecret.Properties,
                                 tmdbApiKeySecret.Properties)
                          .Apply(tuple => new Dictionary<string, string>
                           {
                               [TraktIdSecretOutputName]               = tuple.Item1.SecretUriWithVersion,
                               [TraktSecretSecretOutputName]           = tuple.Item2.SecretUriWithVersion,
                               [TableConnectionStringSecretOutputName] = tuple.Item3.SecretUriWithVersion,
                               [TmdbApiKeySecretOutputName]            = tuple.Item4.SecretUriWithVersion,
                           }.ToImmutableDictionary());
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

        // ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        // ReSharper disable MemberCanBePrivate.Global
        [Output] public Output<ImmutableDictionary<string, string>> SecretsUris { get; set; }
        [Output] public Output<string> ApplicationMd5 { get; set; }
        [Output] public Output<string> FunctionIdentity { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
        // ReSharper restore MemberCanBePrivate.Global
        // ReSharper restore AutoPropertyCanBeMadeGetOnly.Global

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
                    new ProcessStartInfo(
                                         "dotnet",
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

            return new Blob("appPackage", new BlobArgs
            {
                AccessTier           = "Hot",
                Source               = new FileArchive(AppPath),
                StorageAccountName   = mainStorage.Name,
                StorageContainerName = deploymentsCntainer.Name,
                Type                 = "Block",
                Name                 = "application.zip"
            });
        }

        static Secret TmdbApiKeySecret(ResourceGroup resourceGroup, Vault appSecrets, Config config) =>
                new Secret("tmdbApiKeySecret", new SecretArgs
                {
                    Properties = new SecretPropertiesArgs
                    {
                        Attributes = new SecretAttributesArgs
                        {
                            Enabled = true
                        },
                        ContentType = "",
                        Value       = config.RequireSecret("tmdbApiKey")
                    },
                    ResourceGroupName = resourceGroup.Name,
                    SecretName        = "TmdbApiKey",
                    VaultName         = appSecrets.Name
                });

        static Secret TableConnectionStringSecret(
            ResourceGroup resourceGroup,
            Vault appSecrets,
            Output<string> storageConnectionString) =>
                new Secret("tableConnectionString", new SecretArgs
                {
                    Properties = new SecretPropertiesArgs
                    {
                        Attributes = new SecretAttributesArgs
                        {
                            Enabled = true
                        },
                        ContentType = "",
                        Value       = storageConnectionString
                    },
                    ResourceGroupName = resourceGroup.Name,
                    SecretName        = "TableConnectionString",
                    VaultName         = appSecrets.Name
                });

        static Secret TraktSecretSecret(ResourceGroup resourceGroup, Vault appSecrets, Config config) =>
                new Secret("traktClientSecretSecret", new SecretArgs
                {
                    Properties = new SecretPropertiesArgs
                    {
                        Attributes = new SecretAttributesArgs
                        {
                            Enabled = true
                        },
                        ContentType = "",
                        Value       = config.RequireSecret("traktClientSecret")
                    },
                    ResourceGroupName = resourceGroup.Name,
                    SecretName        = "TraktClientSecret",
                    VaultName         = appSecrets.Name
                });

        static Secret TraktIdSecret(ResourceGroup resourceGroup, Vault appSecrets, Config config) =>
                new Secret("traktClientIdSecret", new SecretArgs
                {
                    Properties = new SecretPropertiesArgs
                    {
                        Attributes = new SecretAttributesArgs
                        {
                            Enabled = true
                        },
                        ContentType = "",
                        Value       = config.RequireSecret("traktClientId")
                    },
                    ResourceGroupName = resourceGroup.Name,
                    SecretName        = "TraktClientId",
                    VaultName         = appSecrets.Name
                });

        static Vault KeyVault(
            ResourceGroup resourceGroup,
            Config config,
            GetClientConfigResult azureConfig,
            WebApp functionApp,
            string resourcesPrefix,
            string? savedIdentity) =>
                new Vault("appSecrets",
                          new VaultArgs
                          {
                              Location = resourceGroup.Location,
                              Properties = new VaultPropertiesArgs
                              {
                                  EnableRbacAuthorization = false,
                                  EnableSoftDelete        = false,
                                  AccessPolicies =
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
                                  },
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
                new BlobContainer("deploymentsContainer", new BlobContainerArgs
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
                new WebApp("functionApp", new WebAppArgs
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
                        AppSettings = new Dictionary<string, Input<string>>
                                {
                                    // WEBSITE_RUN_FROM_PACKAGE must stay on top to be ignored if MD5 unchanged
                                    ["WEBSITE_RUN_FROM_PACKAGE"]       = appPackageBlobUrl,

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
                                }.Select(kvp => new NameValuePairArgs {Name = kvp.Key, Value = kvp.Value})
                                 .ToList()
                    }
                }, new CustomResourceOptions
                {
                    IgnoreChanges = md5Unchanged ?
                                    new List<string>
                                    {
                                        // This is why WEBSITE_RUN_FROM_PACKAGE must stay at first position
                                        "siteConfig.appSettings[0].value"
                                    } :
                                    new List<string>()
                });

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
                                      $"AccountKey={tuple.result.Keys.First().Value}")
                      .Apply(Output.CreateSecret);

        static AppServicePlan AppServicePlan(string location, ResourceGroup resourceGroup) =>
                new AppServicePlan("appServicePlan", new AppServicePlanArgs
                {
                    HyperV                    = false,
                    IsSpot                    = false,
                    IsXenon                   = false,
                    Kind                      = "functionapp",
                    Location                  = location,
                    MaximumElasticWorkerCount = 1,
                    Name                      = "WestEuropeLinuxDynamicPlan",
                    PerSiteScaling            = false,
                    Reserved                  = true,
                    ResourceGroupName         = resourceGroup.Name,
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

        static Component AppInsights(ResourceGroup resourceGroup, string resourcesPrefix) =>
                new Component("appInsights", new ComponentArgs
                {
                    ApplicationType   = "web",
                    Kind              = "web",
                    Location          = resourceGroup.Location,
                    ResourceGroupName = resourceGroup.Name,
                    ResourceName      = resourcesPrefix + "ai",
                    RetentionInDays   = 90
                });

        static StorageAccount MainStorage(string resourcesPrefix, ResourceGroup resourceGroup) =>
                new StorageAccount("mainStorage", new StorageAccountArgs
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
                new ResourceGroup("resourceGroup", new ResourceGroupArgs
                {
                    ResourceGroupName = config.Require("resourceGroup"),
                    Location          = location.ToLower().Replace(" ", "")
                });
    }
}