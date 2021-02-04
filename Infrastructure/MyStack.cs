using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
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

// ReSharper disable UnusedMethodReturnValue.Local
#pragma warning disable 618

namespace TvShowRss
{
    class MyStack : Stack
    {
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

            var appInsights = AppInsights(resourceGroup, resourcesPrefix);

            var deploymentsContainer = BlobContainer(mainStorage, resourceGroup);

            var appPackage = AppPackage(mainStorage, deploymentsContainer);

            var appServicePlan = AppServicePlan(location, resourceGroup);

            var storageConnectionString = GetStorageConnectionString(resourceGroup, mainStorage);
            
            var blobUrl = GetAppPackageBlobUrl(deploymentsContainer, appPackage.Url, storageConnectionString);

            var previousMd5 = 
                new StackReference(Deployment.Instance.StackName).Outputs.Apply(d =>
                {
                    var tryGetValue = d.TryGetValue(nameof(ApplicationMd5), out var value);
                    
                    return tryGetValue ? value.ToString()! : null;
                });
            
            var functionApp =
                FunctionApp(config,
                    resourcesPrefix,
                    location,
                    resourceGroup,
                    appServicePlan,
                    appInsights,
                    storageConnectionString,
                    blobUrl,
                    Output.Tuple(previousMd5, appPackage.ContentMd5)
                        .Apply(tuple => tuple.Item1 == tuple.Item2));

            var functionAppName = functionApp.Apply(fa => fa.Name);
            
            GetFeedFunction(functionAppName, resourceGroup);

            AddShowFunction(functionAppName, resourceGroup);

            var appSecrets = KeyVault(resourceGroup, config, azureConfig, functionApp, resourcesPrefix);

            TraktIdSecret(resourceGroup, appSecrets);

            TraktSecretSecret(resourceGroup, appSecrets);

            TableConnectionStringSecret(resourceGroup, appSecrets);

            TmdbApiKeySecret(resourceGroup, appSecrets, config);

            ApplicationMd5 = appPackage.ContentMd5;
        }

        static Output<string> GetAppPackageBlobUrl(BlobContainer deploymentsContainer, Output<string> appPackageUrl,
            Output<string> storageConnectionString) =>
            Output.Tuple(deploymentsContainer.Name, storageConnectionString)
                .Apply(tuple =>
                    GetAccountBlobContainerSAS.InvokeAsync(new GetAccountBlobContainerSASArgs
                    {
                        Start = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                        Expiry = DateTime.Now.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture),
                        ContainerName = tuple.Item1,
                        HttpsOnly = true,
                        ConnectionString = tuple.Item2,
                        Permissions = new GetAccountBlobContainerSASPermissionsArgs
                        {
                            Read = true
                        }
                    })).Apply(x => appPackageUrl.Apply(url => url + x.Sas));

        // ReSharper disable UnusedAutoPropertyAccessor.Global
        // ReSharper disable MemberCanBePrivate.Global
        // ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
        [Output] public Output<string?> ApplicationMd5 { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
        // ReSharper restore MemberCanBePrivate.Global
        // ReSharper restore AutoPropertyCanBeMadeGetOnly.Global

        static ZipBlob AppPackage(StorageAccount mainStorage, BlobContainer deploymentsCntainer)
        {
            var startInfo =
                new ProcessStartInfo(
                    "dotnet",
                    "publish -r linux-x64 -o ../Application/bin/publish ../Application/TvShowRss.csproj")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            var process =
                Process.Start(startInfo)!;

            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception("Application compilation error");

            return new ZipBlob("appPackage", new ZipBlobArgs
            {
                AccessTier = "Hot",
                Content = new FileArchive("../Application/bin/publish"),
                StorageAccountName = mainStorage.Name,
                StorageContainerName = deploymentsCntainer.Name,
                Type = "Block",
                Name = "application.zip"
            });
        }

        static Secret TmdbApiKeySecret(ResourceGroup resourceGroup, Vault appSecrets, Config config)
        {
            return new Secret("tmdbApiKeySecret", new SecretArgs
            {
                Properties = new SecretPropertiesArgs
                {
                    Attributes = new SecretAttributesArgs
                    {
                        Enabled = true
                    },
                    ContentType = "",
                    Value = config.RequireSecret("tmdbApiKey")
                },
                ResourceGroupName = resourceGroup.Name,
                SecretName = "TmdbApiKey",
                VaultName = appSecrets.Name
            });
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
            Output<WebApp> functionApp, string resourcesPrefix)
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
                                ObjectId = functionApp.Apply(fa => fa.Identity).Apply(x =>
                                    x?.PrincipalId ?? ""),//throw new Exception("Missing function identity")),
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
                }, new CustomResourceOptions
                {
                    IgnoreChanges = new List<string>
                    {
                        "properties.accessPolicies"
                    }
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

        static WebAppFunction AddShowFunction(Output<string> functionAppName, ResourceGroup resourceGroup)
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
                    functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/vfs/home/site/wwwroot/AddShow/function.json"),
                FunctionName = "AddShow",
                Href = functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/functions/AddShow"),
                InvokeUrlTemplate = functionAppName.Apply(n => $"https://{n}.azurewebsites.net/api/addshow"),
                IsDisabled = false,
                Language = "DotNetAssembly",
                Name = functionAppName,
                ResourceGroupName = resourceGroup.Name,
                ScriptHref =
                    functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/vfs/home/site/wwwroot/bin/TvShowRss.dll"),
                ScriptRootPathHref =
                    functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/vfs/home/site/wwwroot/AddShow/"),
                TestData = "",
                TestDataHref = functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/vfs/tmp/FunctionsData/AddShow.dat")
            });
        }

        static WebAppFunction GetFeedFunction(Output<string> functionAppName, ResourceGroup resourceGroup)
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
                    functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/vfs/home/site/wwwroot/GetFeed/function.json"),
                FunctionName = "GetFeed",
                Href = functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/functions/GetFeed"),
                InvokeUrlTemplate = functionAppName.Apply(n => $"https://{n}.azurewebsites.net/api/getfeed"),
                IsDisabled = false,
                Language = "DotNetAssembly",
                Name = functionAppName,
                ResourceGroupName = resourceGroup.Name,
                ScriptHref = functionAppName.Apply(n => 
                    $"https://{n}.azurewebsites.net/admin/vfs/home/site/wwwroot/bin/TvShowRss.dll"),
                ScriptRootPathHref = functionAppName.Apply(n => 
                    $"https://{n}.azurewebsites.net/admin/vfs/home/site/wwwroot/GetFeed/"),
                TestData = "",
                TestDataHref = functionAppName.Apply(n => $"https://{n}.azurewebsites.net/admin/vfs/tmp/FunctionsData/GetFeed.dat")
            });
        }

        static Output<WebApp> FunctionApp(Config config, string resourcesPrefix, string location, ResourceGroup resourceGroup,
            AppServicePlan appServicePlan, Component appInsights, Output<string> storageConnectionString,
            Output<string> appPackageBlobUrl, Output<bool> md5Unchanged) =>
            md5Unchanged.Apply(md5U => 
            new WebApp("functionApp", new WebAppArgs
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
                ServerFarmId = appServicePlan.Id.Apply(x => x.Replace("serverFarms", "serverfarms")),
                SiteConfig = new SiteConfigArgs
                {
                    AppSettings = new Dictionary<string, Input<string>>
                        {
                            ["FUNCTIONS_WORKER_RUNTIME"] = "dotnet",
                            ["FUNCTION_APP_EDIT_MODE"] = "readwrite",
                            ["APPINSIGHTS_INSTRUMENTATIONKEY"] = appInsights.InstrumentationKey,
                            ["AzureWebJobsStorage"] = storageConnectionString,
                            ["WEBSITE_RUN_FROM_PACKAGE"] = appPackageBlobUrl,
                            ["TableConnectionString"] = storageConnectionString,
                            ["TraktClientId"] = config.Require("traktClientId"),
                            ["TraktClientSecret"] = config.RequireSecret("traktClientSecret"),
                            ["TmdbApiKey"] = config.RequireSecret("tmdbApiKey")
                            // Key Vault references are not yet available on Linux consumption plans (double check now)
                            //["TraktClientSecret"]            = $"@Microsoft.KeyVault(VaultName={keyVault.Name};SecretName={traktSecret.Name};SecretVersion=Latest)"
                        }.Select(kvp => new NameValuePairArgs {Name = kvp.Key, Value = kvp.Value})
                        .ToList()
                }
            }, new CustomResourceOptions
            {
                IgnoreChanges = md5U ? new List<string>
                {
                    "siteConfig.appSettings"
                } : new List<string>()
            }));

        static Output<string> GetStorageConnectionString(ResourceGroup resourceGroup, StorageAccount mainStorage)
        {
            return Output.Tuple(mainStorage.Name, resourceGroup.Name)
                .Apply(async tuple => (result: await ListStorageAccountKeys.InvokeAsync(new ListStorageAccountKeysArgs
                {
                    AccountName = tuple.Item1,
                    ResourceGroupName = tuple.Item2
                }), accountName: tuple.Item1))
                .Apply(tuple => $"DefaultEndpointsProtocol=https;AccountName={tuple.accountName};" +
                                $"AccountKey={tuple.result.Keys.First().Value}");
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