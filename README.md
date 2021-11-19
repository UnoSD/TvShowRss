# TvShowRss

Azure Function serving an RSS feed with new episode of selected series.

Fully deployable to Azure with Pulumi using SaaS and PaaS only.

Azure Function for hosting and API Management to control rate limits.

# Architecture

![Architecture](https://github.com/UnoSD/TvShowRss/raw/05f84a0b6f2057928a70473a120297896d45fa35/diagram.png)

# How to deploy

Clone the repository, create a `Pulumi.<stackname>.yaml` file in `Infrastructure`:

```yaml
config:
  azure-native:location: <Your preferred Azure location>
  TvShowRss:workloadApplication: tvrss
  TvShowRss:environment: prod
  TvShowRss:tmdbApiKey: <register on TMDB and get an API key for thumbnails>
  TvShowRss:traktClientId: <register on Trakt and get an API client ID>
  TvShowRss:traktClientSecret: <register on Trakt and get an API client secret>
  debug:waitForDebugger: false
```

Run (making sure you are logged in Azure from the Az CLI):

```bash
$ pulumi init <stackname>
$ pulumi up
$ pulumi up # Yes, twice, to circumvent issues with circular dependencies
```

Add your TV shows using their Trakt ID (friends, lost, ...):

```bash
$ curl -d{} -X POST "https://<URL from the Pulumi output>.azure-api.net/tvshowrss/AddShow?subscription-key=<Sub key from your Pulumi outputs>&id=<Trakt name>"
```

Follow the resulting URL (from Pulumi console output) in Feedly or any feed reader of your choice to get notified on new episodes of your TV shows