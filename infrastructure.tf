provider "azurerm" {}

data "azurerm_subscription" "current" {}
data "azurerm_client_config" "current" {}

variable "trakt_client_id" {
  type = string
}

variable "trakt_client_secret" {
  type = string
}

locals {
  resource_group_name             = "TvShowRss"
  location                        = "West Europe"
  resources_prefix                = lower(local.resource_group_name)
  table_cs_secret_name            = "TableConnectionString"
  trakt_client_id_secret_name     = "TraktClientId"
  trakt_client_secret_secret_name = "TraktClientSecret"
  table_name                      = "series"
  sas_expiration_start_date       = "2019-10-21" #timestamp()
  sas_expiration_end_date         = "2019-11-21" #timeadd(timestamp(), "10m")
}

resource "azurerm_resource_group" "tv_show_rss" {
  name     = local.resource_group_name
  location = local.location
}

resource "azurerm_storage_account" "tv_show_rss" {
  name                     = "${local.resources_prefix}sa"
  resource_group_name      = azurerm_resource_group.tv_show_rss.name
  location                 = azurerm_resource_group.tv_show_rss.location
  account_tier             = "Standard"
  account_kind             = "StorageV2"
  account_replication_type = "LRS"
}

resource "azurerm_key_vault" "tv_show_rss" {
  name                        = "${local.resources_prefix}kv"
  location                    = azurerm_resource_group.tv_show_rss.location
  resource_group_name         = azurerm_resource_group.tv_show_rss.name
  tenant_id                   = data.azurerm_subscription.current.tenant_id

  sku_name = "standard"
}

resource "azurerm_key_vault_access_policy" "deployment_user" {
  key_vault_id = azurerm_key_vault.tv_show_rss.id

  object_id = data.azurerm_client_config.current.object_id
  tenant_id = data.azurerm_client_config.current.tenant_id

  secret_permissions = [
    "get",
    "set",
    "list"
  ]
}

resource "azurerm_key_vault_access_policy" "tv_show_rss" {
  key_vault_id = azurerm_key_vault.tv_show_rss.id

  tenant_id = azurerm_function_app.tv_show_rss.identity[0].tenant_id
  object_id = azurerm_function_app.tv_show_rss.identity[0].principal_id

  secret_permissions = [
    "get",
    "set",
    "list"
  ]
}

resource "azurerm_key_vault_secret" "storage_connection_string" {
  name         = local.table_cs_secret_name
  value        = azurerm_storage_account.tv_show_rss.primary_connection_string
  key_vault_id = azurerm_key_vault.tv_show_rss.id
}

resource "azurerm_key_vault_secret" "trakt_client_id" {
  name         = local.trakt_client_id_secret_name
  value        = var.trakt_client_id
  key_vault_id = azurerm_key_vault.tv_show_rss.id
}

resource "azurerm_key_vault_secret" "trakt_client_secret" {
  name         = local.trakt_client_secret_secret_name
  value        = var.trakt_client_secret
  key_vault_id = azurerm_key_vault.tv_show_rss.id
}

resource "azurerm_storage_table" "series" {
  name                 = local.table_name
  storage_account_name = azurerm_storage_account.tv_show_rss.name
}

resource "azurerm_storage_container" "deployments_container" {
  name                  = "deployments"
  storage_account_name  = azurerm_storage_account.tv_show_rss.name
  container_access_type = "private"
}

resource "azurerm_application_insights" "tv_show_rss" {
  name                = "${local.resources_prefix}ai"
  location            = azurerm_resource_group.tv_show_rss.location
  resource_group_name = azurerm_resource_group.tv_show_rss.name
  application_type    = "web"
}

data "archive_file" "tv_show_rss" {
  type = "zip"
  # dotnet publish -r linux-x64 -o bin/publish
  source_dir = "${path.module}/bin/publish"
  output_path = "${path.module}/bin/function_app.zip"
}

resource "azurerm_storage_blob" "tv_show_rss" {
  name = "function_app.${data.archive_file.tv_show_rss.output_md5}.zip"

  storage_account_name   = azurerm_storage_account.tv_show_rss.name
  storage_container_name = azurerm_storage_container.deployments_container.name

  type   = "block"
  source = data.archive_file.tv_show_rss.output_path
}

data "azurerm_storage_account_sas" "tv_show_rss" {
  connection_string = azurerm_storage_account.tv_show_rss.primary_connection_string
  https_only        = true

  resource_types {
    service   = false
    container = false
    object    = true
  }

  services {
    blob  = true
    queue = false
    table = false
    file  = false
  }

  start  = local.sas_expiration_start_date
  expiry = local.sas_expiration_end_date

  permissions {
    read    = true
    write   = false
    delete  = false
    list    = false
    add     = false
    create  = false
    update  = false
    process = false
  }
}

# This is normally auto-generated, remove when Terraform supports
# Dynamic Linux plan
# Import "/subscriptions/${data.azurerm_subscription.current.subscription_id}/resourceGroups/${azurerm_resource_group.tv_show_rss.name}/providers/Microsoft.Web/serverfarms/WestEuropeLinuxDynamicPlan"
resource "azurerm_app_service_plan" "tv_show_rss" {
  location            = azurerm_resource_group.tv_show_rss.location
  name                = "WestEuropeLinuxDynamicPlan" # "${local.resources_prefix}asp"
  resource_group_name = azurerm_resource_group.tv_show_rss.name
  reserved            = true
  kind                = "functionapp"
  
  is_xenon            = false
  
  sku {
    size = "Y1"
    tier = "Dynamic"
  }
}

resource "azurerm_function_app" "tv_show_rss" {
  name                      = "${local.resources_prefix}fa"
  location                  = azurerm_resource_group.tv_show_rss.location
  resource_group_name       = azurerm_resource_group.tv_show_rss.name
  app_service_plan_id       = azurerm_app_service_plan.tv_show_rss.id
  storage_connection_string = azurerm_storage_account.tv_show_rss.primary_connection_string
  https_only                = true
  enable_builtin_logging    = false
  version                   = "~2"
  app_settings = {
    FUNCTIONS_WORKER_RUNTIME                 = "dotnet"
    FUNCTIONS_EXTENSION_VERSION              = "~2"
    FUNCTION_APP_EDIT_MODE                   = "readwrite"
    APPINSIGHTS_INSTRUMENTATIONKEY           = azurerm_application_insights.tv_show_rss.instrumentation_key
    AzureWebJobsStorage                      = azurerm_storage_account.tv_show_rss.primary_connection_string
    WEBSITE_CONTENTAZUREFILECONNECTIONSTRING = azurerm_storage_account.tv_show_rss.primary_connection_string
    WEBSITE_CONTENTSHARE                     = local.resources_prefix
    WEBSITE_USE_ZIP                          = "https://${azurerm_storage_account.tv_show_rss.name}.blob.core.windows.net/${azurerm_storage_container.deployments_container.name}/${azurerm_storage_blob.tv_show_rss.name}${data.azurerm_storage_account_sas.tv_show_rss.sas}"
    TableConnectionString                    = azurerm_storage_account.tv_show_rss.primary_connection_string
    TraktClientId                            = var.trakt_client_id
    TraktClientSecret                        = var.trakt_client_secret
    # Key Vault references are not yet available on Linux consumption plans
    #TraktClientId                            = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.tv_show_rss.name};SecretName=${azurerm_key_vault_secret.trakt_client_id.name};SecretVersion=${azurerm_key_vault_secret.trakt_client_id.version})"
    #TraktClientSecret                        = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.tv_show_rss.name};SecretName=${azurerm_key_vault_secret.trakt_client_secret.name};SecretVersion=${azurerm_key_vault_secret.trakt_client_secret.version})"
  }
  site_config {
    use_32_bit_worker_process = false
  }
  identity {
    type = "SystemAssigned"
  }
}