provider "azurerm" {}

data "azurerm_subscription" "current" {}

variable "trakt_client_id" {
  type = string
}

variable "trakt_client_secret" {
  type = string
}

locals {
  resource_group_name = "TvShowRss"
}

resource "azurerm_resource_group" "tv_show_rss" {
  name     = local.resource_group_name
  location = "West Europe"
}

resource "azurerm_storage_account" "tv_show_rss" {
  name                     = "tvshowrsssa"
  resource_group_name      = azurerm_resource_group.tv_show_rss.name
  location                 = azurerm_resource_group.tv_show_rss.location
  account_tier             = "Standard"
  account_kind             = "StorageV2"
  account_replication_type = "LRS"
}

resource "azurerm_key_vault" "tv_show_rss" {
  name                        = "tvshowrsskv"
  location                    = azurerm_resource_group.tv_show_rss.location
  resource_group_name         = azurerm_resource_group.tv_show_rss.name
  tenant_id                   = data.azurerm_subscription.current.tenant_id

  sku_name = "standard"

  access_policy {
    tenant_id = data.azurerm_subscription.current.tenant_id # azurerm_function_app.tv_show_rss.identity[0].tenant_id
    object_id = azurerm_function_app.tv_show_rss.identity[0].principal_id // Function App MI

    secret_permissions = [
      "get",
      "set",
      "list"
    ]
  }
}

resource "azurerm_key_vault_secret" "storage_connection_string" {
  name         = "TableConnectionString"
  value        = azurerm_storage_account.tv_show_rss.primary_connection_string
  key_vault_id = azurerm_key_vault.tv_show_rss.id
}

resource "azurerm_key_vault_secret" "trakt_client_id" {
  name         = "TraktClientId"
  value        = var.trakt_client_id
  key_vault_id = azurerm_key_vault.tv_show_rss.id
}

resource "azurerm_key_vault_secret" "trakt_client_secret" {
  name         = "TraktClientSecret"
  value        = var.trakt_client_secret
  key_vault_id = azurerm_key_vault.tv_show_rss.id
}

resource "azurerm_storage_table" "series" {
  name                 = "series"
  storage_account_name = azurerm_storage_account.tv_show_rss.name
}

resource "azurerm_storage_container" "deployments_container" {
  name                  = "deployments"
  storage_account_name  = azurerm_storage_account.tv_show_rss.name
  container_access_type = "private"
}

resource "azurerm_application_insights" "tv_show_rss" {
  name                = "tvshowrssai"
  location            = azurerm_resource_group.tv_show_rss.location
  resource_group_name = azurerm_resource_group.tv_show_rss.name
  application_type    = "web"
}

data "archive_file" "tv_show_rss" {
  type = "zip"
  # Publish linux 64 bit? dotnet publish -r linux-x64 -o bin/publish
  # Used results of running `func azure functionapp publish test123xx`
  source_dir = "${path.module}/bin/publish"
  // Should be hash of itself, not timestamp
  output_path = "${path.module}/bin/function_app.${formatdate("DDMMYYhhmmss", timestamp())}.zip"
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

  start  = timestamp()
  expiry = timeadd(timestamp(), "10m")

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

resource "azurerm_function_app" "tv_show_rss" {
  name                      = "tvshowrssfa"
  location                  = azurerm_resource_group.tv_show_rss.location
  resource_group_name       = azurerm_resource_group.tv_show_rss.name
  app_service_plan_id       = "/subscriptions/${data.azurerm_subscription.current.subscription_id}/resourceGroups/${azurerm_resource_group.tv_show_rss.name}/providers/Microsoft.Web/serverfarms/WestEuropeLinuxDynamicPlan"
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
    WEBSITE_CONTENTSHARE                     = "tvshowrss"
    WEBSITE_USE_ZIP                          = "https://${azurerm_storage_account.tv_show_rss.name}.blob.core.windows.net/${azurerm_storage_container.deployments_container.name}/${azurerm_storage_blob.tv_show_rss.name}${data.azurerm_storage_account_sas.tv_show_rss.sas}"
  }
  site_config {
    use_32_bit_worker_process = false
  }
  identity {
    type = "SystemAssigned"
  }
}