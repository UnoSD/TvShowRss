using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Pulumi;
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
using SkuName = Pulumi.AzureNextGen.KeyVault.Latest.SkuName;

// ReSharper disable UnusedMethodReturnValue.Local

namespace TvShowRss
{
    class MyStack : Stack
    {
        [SuppressMessage("ReSharper", "ObjectCreationAsStatement")]
        public MyStack()
        {
            var config = new Config("azure");
            var resourcesPrefix = config.Require("resourcesPrefix");
            var location = config.Require("location");
            var azureConfig = GetClientConfig.InvokeAsync().Result;

            var resourceGroup = ResourceGroup(config, location);

            var mainStorage = MainStorage(resourcesPrefix, resourceGroup);

            AppInsights(resourceGroup, resourcesPrefix);

            var appServicePlan = AppServicePlan(location, resourceGroup);

            var functionApp = FunctionApp(config, resourcesPrefix, location, resourceGroup, appServicePlan);

            GetFeedFunction(resourcesPrefix, resourceGroup);

            AddShowFunction(resourcesPrefix, resourceGroup);

            BlobContainer(mainStorage, resourceGroup);

            var appSecrets = KeyVault(resourceGroup, config, azureConfig, functionApp, resourcesPrefix);

            TraktIdSecret(resourceGroup, appSecrets);

            TraktSecretSecret(resourceGroup, appSecrets);

            TableConnectionStringSecret(resourceGroup, appSecrets);

            // var appPackage = new FileArchive("../bin/publish");
            //
            // var keyValuePairs = Directory.EnumerateFileSystemEntries("../bin/publish")
            //     .Select(x => new KeyValuePair<string,AssetOrArchive>(x, new FileArchive(x)));
            //
            // var assetArchive = 
            //     new AssetArchive(new Dictionary<string, AssetOrArchive>(keyValuePairs));
        }

        static Secret TableConnectionStringSecret(ResourceGroup resourceGroup, Vault appSecrets)
        {
            return new Secret("tableConnectionString", new SecretArgs
            {
                Properties = new SecretPropertiesArgs
                {
                    Attributes = new SecretAttributesArgs
                    {
                        Enabled = true
                    },
                    ContentType = ""
                },
                ResourceGroupName = resourceGroup.Name,
                SecretName = "TableConnectionString",
                VaultName = appSecrets.Name
            });
        }

        static Secret TraktSecretSecret(ResourceGroup resourceGroup, Vault appSecrets)
        {
            return new Secret("traktClientSecretSecret", new SecretArgs
            {
                Properties = new SecretPropertiesArgs
                {
                    Attributes = new SecretAttributesArgs
                    {
                        Enabled = true
                    },
                    ContentType = ""
                },
                ResourceGroupName = resourceGroup.Name,
                SecretName = "TraktClientSecret",
                VaultName = appSecrets.Name
            });
        }

        static Secret TraktIdSecret(ResourceGroup resourceGroup, Vault appSecrets)
        {
            return new Secret("traktClientIdSecret", new SecretArgs
            {
                Properties = new SecretPropertiesArgs
                {
                    Attributes = new SecretAttributesArgs
                    {
                        Enabled = true
                    },
                    ContentType = ""
                    //Value = config.RequireSecret("traktClientId")
                },
                ResourceGroupName = resourceGroup.Name,
                SecretName = "TraktClientId",
                VaultName = appSecrets.Name
            });
        }

        static Vault KeyVault(ResourceGroup resourceGroup, Config config, GetClientConfigResult azureConfig,
            WebApp functionApp, string resourcesPrefix)
        {
            return new Vault("appSecrets",
                new VaultArgs
                {
                    Location = resourceGroup.Location,
                    Properties = new VaultPropertiesArgs
                    {
                        EnableRbacAuthorization = false,
                        EnableSoftDelete = false,
                        AccessPolicies =
                        {
                            new AccessPolicyEntryArgs
                            {
                                //ObjectId = azureConfig.ObjectId,
                                ObjectId = config.Require("keyVaultManagerObjectId"),
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
                            },
                            new AccessPolicyEntryArgs
                            {
                                ObjectId = functionApp.Identity.Apply(x =>
                                    x?.PrincipalId ?? throw new Exception("Missing function identity")),
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
                        EnabledForDeployment = false,
                        EnabledForDiskEncryption = false,
                        EnabledForTemplateDeployment = false,
                        ProvisioningState = "Succeeded",
                        Sku = new Pulumi.AzureNextGen.KeyVault.Latest.Inputs.SkuArgs
                        {
                            Family = "A",
                            Name = SkuName.Standard
                        },
                        TenantId = azureConfig.TenantId,
                        VaultUri = $"https://{resourcesPrefix + "kv"}.vault.azure.net/"
                    },
                    ResourceGroupName = resourceGroup.Name,
                    VaultName = resourcesPrefix + "kv"
                });
        }

        static BlobContainer BlobContainer(StorageAccount mainStorage, ResourceGroup resourceGroup)
        {
            return new BlobContainer("deploymentsContainer", new BlobContainerArgs
            {
                AccountName = mainStorage.Name,
                ContainerName = "deployments",
                DefaultEncryptionScope = "$account-encryption-key",
                DenyEncryptionScopeOverride = false,
                PublicAccess = PublicAccess.None,
                ResourceGroupName = resourceGroup.Name
            });
        }

        static WebAppFunction AddShowFunction(string resourcesPrefix, ResourceGroup resourceGroup)
        {
            return new WebAppFunction("addShowFunction", new WebAppFunctionArgs
            {
                Config = new Dictionary<string, object>
                {
                    ["bindings"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["authLevel"] = "function",
                            ["methods"] = new[] {"post"},
                            ["name"] = "req",
                            ["type"] = "httpTrigger"
                        }.ToImmutableDictionary()
                    },
                    ["configurationSource"] = "attributes",
                    ["disabled"] = false,
                    ["entryPoint"] = "TvShowRss.AddShow.Run",
                    ["generatedBy"] = "Microsoft.NET.Sdk.Functions-3.0.11",
                    ["scriptFile"] = "../bin/TvShowRss.dll"
                }.ToImmutableDictionary(),
                ConfigHref =
                    $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/home/site/wwwroot/AddShow/function.json",
                FunctionName = "AddShow",
                Href = $"https://{resourcesPrefix}fa.azurewebsites.net/admin/functions/AddShow",
                InvokeUrlTemplate = $"https://{resourcesPrefix}fa.azurewebsites.net/api/addshow",
                IsDisabled = false,
                Language = "DotNetAssembly",
                Name = resourcesPrefix + "fa",
                ResourceGroupName = resourceGroup.Name,
                ScriptHref =
                    $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/home/site/wwwroot/bin/TvShowRss.dll",
                ScriptRootPathHref =
                    $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/home/site/wwwroot/AddShow/",
                TestData = "",
                TestDataHref = $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/tmp/FunctionsData/AddShow.dat"
            });
        }

        static WebAppFunction GetFeedFunction(string resourcesPrefix, ResourceGroup resourceGroup)
        {
            return new WebAppFunction("getFeedFunction", new WebAppFunctionArgs
            {
                Config = new Dictionary<string, object>
                {
                    ["bindings"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["authLevel"] = "function",
                            ["methods"] = new[] {"get"},
                            ["name"] = "req",
                            ["type"] = "httpTrigger"
                        }.ToImmutableDictionary()
                    },
                    ["configurationSource"] = "attributes",
                    ["disabled"] = false,
                    ["entryPoint"] = "TvShowRss.GetFeed.Run",
                    ["generatedBy"] = "Microsoft.NET.Sdk.Functions-3.0.11",
                    ["scriptFile"] = "../bin/TvShowRss.dll"
                }.ToImmutableDictionary(),
                ConfigHref =
                    $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/home/site/wwwroot/GetFeed/function.json",
                FunctionName = "GetFeed",
                Href = $"https://{resourcesPrefix}fa.azurewebsites.net/admin/functions/GetFeed",
                InvokeUrlTemplate = $"https://{resourcesPrefix}fa.azurewebsites.net/api/getfeed",
                IsDisabled = false,
                Language = "DotNetAssembly",
                Name = resourcesPrefix + "fa",
                ResourceGroupName = resourceGroup.Name,
                ScriptHref =
                    $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/home/site/wwwroot/bin/TvShowRss.dll",
                ScriptRootPathHref =
                    $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/home/site/wwwroot/GetFeed/",
                TestData = "",
                TestDataHref = $"https://{resourcesPrefix}fa.azurewebsites.net/admin/vfs/tmp/FunctionsData/GetFeed.dat"
            });
        }

        static WebApp FunctionApp(Config config, string resourcesPrefix, string location, ResourceGroup resourceGroup,
            AppServicePlan appServicePlan)
        {
            return new WebApp("functionApp", new WebAppArgs
            {
                ClientAffinityEnabled = false,
                ClientCertEnabled = false,
                ClientCertMode = ClientCertMode.Required,
                ContainerSize = 1536,
                CustomDomainVerificationId = config.Require("customDomainVerificationId"),
                DailyMemoryTimeQuota = 0,
                Enabled = true,
                HostNameSslStates =
                {
                    new HostNameSslStateArgs
                    {
                        HostType = HostType.Standard,
                        Name = resourcesPrefix + "fa.azurewebsites.net",
                        SslState = SslState.Disabled
                    },
                    new HostNameSslStateArgs
                    {
                        HostType = HostType.Repository,
                        Name = resourcesPrefix + "fa.scm.azurewebsites.net",
                        SslState = SslState.Disabled
                    }
                },
                HostNamesDisabled = false,
                HttpsOnly = true,
                HyperV = false,
                Identity = new ManagedServiceIdentityArgs
                {
                    Type = ManagedServiceIdentityType.SystemAssigned
                },
                IsXenon = false,
                Kind = "functionapp,linux",
                Location = location,
                Name = resourcesPrefix + "fa",
                RedundancyMode = RedundancyMode.None,
                Reserved = true,
                ResourceGroupName = resourceGroup.Name,
                ScmSiteAlsoStopped = false,
                ServerFarmId = appServicePlan.Id.Apply(x => x.Replace("serverFarms", "serverfarms"))
            });
        }

        static AppServicePlan AppServicePlan(string location, ResourceGroup resourceGroup)
        {
            return new AppServicePlan("appServicePlan", new AppServicePlanArgs
            {
                HyperV = false,
                IsSpot = false,
                IsXenon = false,
                Kind = "functionapp",
                Location = location,
                MaximumElasticWorkerCount = 1,
                Name = "WestEuropeLinuxDynamicPlan",
                PerSiteScaling = false,
                Reserved = true,
                ResourceGroupName = resourceGroup.Name,
                Sku = new SkuDescriptionArgs
                {
                    Capacity = 0,
                    Family = "Y",
                    Name = "Y1",
                    Size = "Y1",
                    Tier = "Dynamic"
                },
                TargetWorkerCount = 0,
                TargetWorkerSizeId = 0
            });
        }

        static Component AppInsights(ResourceGroup resourceGroup, string resourcesPrefix)
        {
            return new Component("appInsights", new ComponentArgs
            {
                ApplicationType = "web",
                Kind = "web",
                Location = resourceGroup.Location,
                ResourceGroupName = resourceGroup.Name,
                ResourceName = resourcesPrefix + "ai",
                RetentionInDays = 90
            });
        }

        static StorageAccount MainStorage(string resourcesPrefix, ResourceGroup resourceGroup)
        {
            return new StorageAccount("mainStorage", new StorageAccountArgs
            {
                AccessTier = AccessTier.Hot,
                AccountName = resourcesPrefix + "sa",
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
                Kind = "StorageV2",
                Location = resourceGroup.Location,
                NetworkRuleSet = new Pulumi.AzureNextGen.Storage.Latest.Inputs.NetworkRuleSetArgs
                {
                    Bypass = "AzureServices",
                    DefaultAction = DefaultAction.Allow
                },
                ResourceGroupName = resourceGroup.Name,
                Sku = new Pulumi.AzureNextGen.Storage.Latest.Inputs.SkuArgs
                {
                    Name = "Standard_LRS"
                }
            });
        }

        static ResourceGroup ResourceGroup(Config config, string location)
        {
            return new ResourceGroup("resourceGroup", new ResourceGroupArgs
            {
                ResourceGroupName = config.Require("resourceGroup"),
                Location = location.ToLower().Replace(" ", "")
            });
        }
    }
}