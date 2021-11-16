using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Pulumi;
using Pulumi.AzureAD;
using Pulumi.AzureAD.Inputs;
using Pulumi.AzureNative.ApiManagement;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Insights;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.KeyVault.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Storage.Inputs;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.Random;
using ApiManagementServiceSkuPropertiesArgs =
    Pulumi.AzureNative.ApiManagement.Inputs.ApiManagementServiceSkuPropertiesArgs;
using Config = Pulumi.Config;
using Deployment = Pulumi.Deployment;
using ManagedServiceIdentityType = Pulumi.AzureNative.Web.ManagedServiceIdentityType;
using SkuName = Pulumi.AzureNative.KeyVault.SkuName;
using Table = Pulumi.AzureNative.Storage.Table;
using TableArgs = Pulumi.AzureNative.Storage.TableArgs;
using ApiManagementServiceIdentityArgs =
    Pulumi.AzureNative.ApiManagement.Inputs.ApiManagementServiceIdentityArgs;
using GetClientConfig = Pulumi.AzureNative.Authorization.GetClientConfig;
using GetClientConfigResult = Pulumi.AzureNative.Authorization.GetClientConfigResult;
using SecretArgs = Pulumi.AzureNative.KeyVault.SecretArgs;

// Backlog
// Add Git commit to the infrastructure tags to trace back to the deployed version
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
            new(() => new StackReference(Deployment.Instance.StackName));

        static StackReference Stack => LazyStack.Value;

        const string SecretsUris = nameof(SecretsUris);
        const string ApplicationMd5 = nameof(ApplicationMd5);
        const string FunctionIdentity = nameof(FunctionIdentity);
        const string ApimIdentity = nameof(ApimIdentity);
        const string AadFunctionClientId = nameof(AadFunctionClientId);
        const string GetFeedFunctionName = nameof(GetFeed);
        const string AddShowFunctionName = nameof(AddShow);

        const string StorageBlobDataReaderRoleId = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1";
        
        const string MicrosoftGraphResourceAppId = "00000003-0000-0000-c000-000000000000";
        const string MicrosoftGraphUserReadId = "e1fe6dd8-ba31-4d61-89e7-88639da4683d";

        static Task<int> Main() => Deployment.RunAsync(async () =>
        {
            WaitForDebuggerIfRequested();

            var config = new Config();
            var workloadApplication = config.Require("workloadApplication");
            var environment = config.Require("environment");
            var azureConfig = await GetClientConfig.InvokeAsync();

            var locationName =
                new Config("azure-native").Require("location")
                                          .ToLowerInvariant()
                                          .Replace(" ", "")
                                          .Replace("europe", "eu");

            string Name(string type, bool noDashes = false) => 
                noDashes ?
                    $"{type}{workloadApplication}{environment}{locationName}{1:000}" :
                    $"{type}-{workloadApplication}-{environment}-{locationName}-{1:000}" ; 
            
            var resourceGroup = CreateResourceGroup(Name("rg"));

            var storageAccount = CreateStorage(Name("sa", true), resourceGroup);

            CreateSeriesTable(resourceGroup, storageAccount);

            var appInsights = CreateAppInsights(resourceGroup, Name("ai"));

            var deploymentsContainer = CreateDeploymentsBlobContainer(storageAccount, resourceGroup);

            var appPackage = CreateAppPackageBlob(storageAccount, deploymentsContainer, resourceGroup);

            var appServicePlan = CreateAppServicePlan(resourceGroup, Name("asp"));

            var storageConnectionString = GetStorageConnectionString(resourceGroup, storageAccount);

            var previousMd5 = await Stack.GetStringAsync(nameof(ApplicationMd5));

            var secretUris =
                (await Stack.GetAsync<ImmutableDictionary<string, object>>(nameof(SecretsUris)))?.CastValue<string>();

            var appSourceMd5 = GetAppSourceMd5(AppPath);

            var functionApp =
                FunctionApp(Name("fa"),
                            resourceGroup,
                            appServicePlan.Id,
                            appPackage.Url,
                            appSourceMd5 == previousMd5,
                            key => GetKeyVaultReference(secretUris, key));

            var applicationClientId =
                await Stack.GetStringAsync(nameof(AadFunctionClientId));

            var aadFunctionApp = new Application("functionApp", new ApplicationArgs
            {
                Api = new ApplicationApiArgs
                {
                    Oauth2PermissionScopes = 
                    {
                        new ApplicationApiOauth2PermissionScopeArgs
                        {
                            AdminConsentDescription = Output.Format($"Allow the application to access {functionApp.Name} on behalf of the signed-in user."),
                            AdminConsentDisplayName = Output.Format($"Access {functionApp.Name}"),
                            Enabled = true,
                            Id = new RandomUuid("permissionScope").Result,
                            Type = "User",
                            UserConsentDescription = Output.Format($"Allow the application to access {functionApp.Name} on your behalf."),
                            UserConsentDisplayName = Output.Format($"Access {functionApp.Name}"),
                            Value = "user_impersonation"
                        }
                    }
                },
                DisplayName = functionApp.Name,
                IdentifierUris = applicationClientId is null ? new InputList<string>() : new List<Input<string>>
                {
                    Output.Format($"api://{applicationClientId}")
                },
                Owners = 
                {
                    azureConfig.ObjectId
                },
                PreventDuplicateNames = false,
                RequiredResourceAccesses = 
                {
                    new ApplicationRequiredResourceAccessArgs
                    {
                        ResourceAccesses = 
                        {
                            new ApplicationRequiredResourceAccessResourceAccessArgs
                            {
                                Id = MicrosoftGraphUserReadId,
                                Type = "Scope"
                            }
                        },
                        ResourceAppId = MicrosoftGraphResourceAppId
                    }
                },
                SignInAudience = "AzureADMyOrg",
                Web = new ApplicationWebArgs
                {
                    HomepageUrl = Output.Format($"https://{functionApp.DefaultHostName}"),
                    ImplicitGrant = new ApplicationWebImplicitGrantArgs
                    {
                        IdTokenIssuanceEnabled = true
                    },
                    RedirectUris = 
                    {
                        Output.Format($"https://{functionApp.DefaultHostName}/.auth/login/aad/callback")
                    }
                }
            });
            
            // Will need to use this to force the patch invocation below
            var applicationIdAfterUpdate =
                Output.Tuple(aadFunctionApp.ObjectId, aadFunctionApp.ApplicationId).Apply(async tuple =>
                {
                    // accessTokenAcceptedVersion set to 2 (upload app manifest?)
                    // https://docs.microsoft.com/en-us/graph/api/resources/apiapplication?view=graph-rest-beta //requestedAccessTokenVersion 
                    var content = JsonContent.Create(new
                    {
                        api = new
                        {
                            requestedAccessTokenVersion = 2
                        }
                    });

                    var result = await GetClientToken.InvokeAsync(new GetClientTokenArgs
                    {
                        Endpoint = "https://graph.microsoft.com"
                    });

                    var httpClient = new HttpClient
                    {
                        DefaultRequestHeaders =
                        {
                            Authorization = AuthenticationHeaderValue.Parse($"Bearer {result.Token}")
                        },
                        BaseAddress = new Uri("https://graph.microsoft.com")
                    };
                    
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

                    var callResult = await httpClient.PatchAsync($"/v1.0/applications/{tuple.Item1}", content);

                    callResult.EnsureSuccessStatusCode();
                    
                    return tuple.Item2;
                });
            
            new WebAppAuthSettings("functionAuth", new WebAppAuthSettingsArgs
            {
                Name = functionApp.Name,
                ResourceGroupName = resourceGroup.Name,
                AllowedAudiences = 
                {
                    Output.Format($"api://{applicationIdAfterUpdate}")
                },
                ClientId                    = applicationIdAfterUpdate,
                ClientSecretSettingName     = "MICROSOFT_PROVIDER_AUTHENTICATION_SECRET",
                ConfigVersion               = "v2",
                Enabled                     = true,
                IsAuthFromFile              = "False",
                Issuer                      = Output.Format($"https://sts.windows.net/{azureConfig.TenantId}/v2.0"), // Maybe remove v2.0 if we can't set aad app above to issue the 2.0
                TokenStoreEnabled           = true,
                UnauthenticatedClientAction = UnauthenticatedClientAction.RedirectToLoginPage
            });
            
            var savedFunctionAppIdentity =
                await Stack.GetStringAsync(FunctionIdentity);
            
            var functionAppIdentity =
                functionApp.Identity.Apply(x => x?.PrincipalId ??
                                                // Workaround for a Pulumi issue that forgets the function identity
                                                savedFunctionAppIdentity ??
                                                //throw new Exception("Missing function identity")),
                                                "Missing function identity, file a bug in Pulumi");

            new RoleAssignment("functionAppBlobReader", new RoleAssignmentArgs
            {
                PrincipalId = functionAppIdentity,
                Scope = storageAccount.Id,
                RoleDefinitionId = $"/subscriptions/{azureConfig.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{StorageBlobDataReaderRoleId}",
                PrincipalType = PrincipalType.ServicePrincipal
            });
            
            var appSecrets = KeyVault(resourceGroup,
                                      azureConfig,
                                      functionAppIdentity,
                                      Name("kv", true),
                                      await Stack.GetStringAsync(ApimIdentity));

            var traktIdSecret = SecretFromConfig(resourceGroup, appSecrets, config, TraktClientId.ToCamelCase());

            var traktSecretSecret =
                SecretFromConfig(resourceGroup, appSecrets, config, TraktClientSecret.ToCamelCase());

            var tableConnectionStringSecret =
                Secret(resourceGroup, appSecrets, storageConnectionString, TableConnectionString.ToCamelCase());

            var tmdbApiKeySecret = SecretFromConfig(resourceGroup, appSecrets, config, TmdbApiKey.ToCamelCase());

            var appInsightKeySecret =
                Secret(resourceGroup, appSecrets, appInsights.InstrumentationKey, AppInsightKey.ToCamelCase());

            var apiManagement = ApiManagement(Name("apim"), resourceGroup);

            var backend = FunctionAppApimBackend(functionApp, resourceGroup, apiManagement);

            var api = ApimApi(apiManagement, resourceGroup);

            AllOperationsPolicy(api, apiManagement, resourceGroup, backend, aadFunctionApp);

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
                ["ApimTestResult"]      = TestUrl(getFeedUrl, isSecondRun),
                ["FunctionOutboundIPs"] = functionApp.OutboundIpAddresses,
                ["GetFeedURL"]          = getFeedUrl,
                ["AddShowURL"]          = GetAddShowUrl(apiManagement, api, addShow, apimSubscription),
                [AadFunctionClientId]   = aadFunctionApp.ApplicationId
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
            new("apimFunctionSubscription",
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
            ApiManagementService apiManagement) =>
            new("apiManagementFunctionBackend",
                new BackendArgs
                {
                    BackendId         = functionApp.Name,
                    ResourceGroupName = resourceGroup.Name,
                    ServiceName       = apiManagement.Name,
                    Protocol          = BackendProtocol.Http,
                    Url               = Output.Format($"https://{functionApp.DefaultHostName}/api")
                });

        static ApiOperation CreateApiOperation(
            string operationName,
            Api api,
            ResourceGroup resourceGroup,
            ApiManagementService apim,
            HttpMethod httpMethod) =>
            new(ToCamelCase(operationName) + "Operation",
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

        static void AllOperationsPolicy(Api api, ApiManagementService apim, ResourceGroup resourceGroup, Backend be, Application aadFunctionApp) =>
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
        <authentication-managed-identity resource=""api://{aadFunctionApp.ApplicationId}"" />
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
            new("api",
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
            string name,
            ResourceGroup resourceGroup) =>
            new(name,
                new ApiManagementServiceArgs
                {
                    ResourceGroupName = resourceGroup.Name,
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

        static Output<string> TestUrl(Output<string> url, bool isSecondRun) =>
            isSecondRun ?
                url.Apply(x => new HttpClient().GetAsync(x))
                   .Apply(async response => response.IsSuccessStatusCode ?
                              "Successful" :
                              $"Failed with: {response.StatusCode} {await response.Content.ReadAsStringAsync()}") :
                Output.Create("Run the deployment again to set app settings to correct Key Vault references");

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
                    ct => ct.TransformFinalBlock(content, 0, content.Length) :
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

            return BitConverter.ToString(md5.Hash!);
        }

        static Blob CreateAppPackageBlob(StorageAccount mainStorage, BlobContainer deploymentsCntainer, ResourceGroup resourceGroup)
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
                                AccessTier        = BlobAccessTier.Hot,
                                Source            = new FileArchive(AppPath),
                                AccountName       = mainStorage.Name,
                                ContainerName     = deploymentsCntainer.Name,
                                Type              = BlobType.Block,
                                BlobName          = "application.zip",
                                ResourceGroupName = resourceGroup.Name
                            });
        }

        static Secret SecretFromConfig(
            ResourceGroup resourceGroup,
            Vault appSecrets,
            Config config,
            string configKey) =>
            Secret(resourceGroup, appSecrets, config.RequireSecret(configKey), configKey);

        static Secret Secret(ResourceGroup resourceGroup, Vault appSecrets, Output<string> value, string name) =>
            new(name + "Secret",
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
            Output<string> functionAppIdentity,
            string name,
            string? apimIdentity) =>
            new(name,
                new VaultArgs
                {
                    VaultName = GetPseudoRandomStringFor(name),
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
                                        ObjectId = functionAppIdentity,
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
                        Sku = new Pulumi.AzureNative.KeyVault.Inputs.SkuArgs
                        {
                            Family = "A",
                            Name   = SkuName.Standard
                        },
                        TenantId = azureConfig.TenantId,
                    },
                    ResourceGroupName = resourceGroup.Name
                });

        static Output<string> GetPseudoRandomStringFor(string name, int length = 4) =>
            new RandomId(name, new RandomIdArgs
            {
                ByteLength = 8
            }).Hex.Apply(x => $"{name}{x.ToLower()[..length]}");

        static BlobContainer CreateDeploymentsBlobContainer(StorageAccount mainStorage, ResourceGroup resourceGroup) =>
            new("deploymentsContainer",
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
            string name,
            ResourceGroup resourceGroup,
            Output<string> appServicePlanId,
            Output<string> appPackageBlobUrl,
            bool md5Unchanged,
            Func<string, string> getKeyVaultReference) =>
            new(name,
                new WebAppArgs
                {
                    ClientAffinityEnabled = false,
                    HttpsOnly             = true,
                    Identity = new ManagedServiceIdentityArgs
                    {
                        Type = ManagedServiceIdentityType.SystemAssigned
                    },
                    Kind              = "functionapp,linux",
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
            new()
            {
                // WEBSITE_RUN_FROM_PACKAGE must stay on top to be ignored if MD5 unchanged
                ["WEBSITE_RUN_FROM_PACKAGE"]       = appPackageBlobUrl,

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
            ResourceGroup resourceGroup,
            string name) =>
            new(name,
                new AppServicePlanArgs
                {
                    HyperV                    = false,
                    IsSpot                    = false,
                    IsXenon                   = false,
                    Kind                      = "functionapp",
                    MaximumElasticWorkerCount = 1,
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

        static Component CreateAppInsights(ResourceGroup resourceGroup, string name) =>
            new(name,
                new ComponentArgs
                {
                    ApplicationType   = "web",
                    Kind              = "web",
                    ResourceGroupName = resourceGroup.Name,
                    RetentionInDays   = 90
                });

        static StorageAccount CreateStorage(string name, ResourceGroup resourceGroup) =>
            new(name,
                new StorageAccountArgs
                {
                    AccessTier             = AccessTier.Hot,
                    EnableHttpsTrafficOnly = false,
                    AccountName = GetPseudoRandomStringFor(name),
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
                    NetworkRuleSet = new Pulumi.AzureNative.Storage.Inputs.NetworkRuleSetArgs
                    {
                        Bypass        = "AzureServices",
                        DefaultAction = DefaultAction.Allow
                    },
                    ResourceGroupName = resourceGroup.Name,
                    Sku = new Pulumi.AzureNative.Storage.Inputs.SkuArgs
                    {
                        Name = "Standard_LRS"
                    }
                });

        static ResourceGroup CreateResourceGroup(string name) =>
            new(name, new ResourceGroupArgs());
    }
}