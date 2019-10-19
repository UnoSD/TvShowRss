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
    tenant_id = data.azurerm_subscription.current.tenant_id
    object_id = "cf404d16-1f86-4648-abdd-25fc63f6f7db" // Function App MI

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