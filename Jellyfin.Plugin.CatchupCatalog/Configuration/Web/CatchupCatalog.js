export default function (view) {
  const pluginId = "6fb961ad-51d1-42c2-a1c3-49e5a9458c68";

  view.addEventListener("viewshow", () => {
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then((config) => {
      view.querySelector("#Enabled").checked = config.Enabled;
      view.querySelector("#BaseUrl").value = config.BaseUrl || "";
      view.querySelector("#Username").value = config.Username || "";
      view.querySelector("#Password").value = config.Password || "";
      view.querySelector("#ArchiveDays").value = config.ArchiveDays || 7;
      view.querySelector("#CacheMinutes").value = config.CacheMinutes || 30;
      view.querySelector("#MetadataLanguage").value = config.MetadataLanguage || "he-IL";
      view.querySelector("#TmdbBearerToken").value = config.TmdbBearerToken || "";
      view.querySelector("#ShowMovies").checked = config.ShowMovies;
      view.querySelector("#ShowSeries").checked = config.ShowSeries;
      view.querySelector("#ShowPrograms").checked = config.ShowPrograms;
      Dashboard.hideLoadingMsg();
    });
  });

  view.querySelector("#CatchupCatalogForm").addEventListener("submit", (event) => {
    event.preventDefault();
    Dashboard.showLoadingMsg();

    ApiClient.getPluginConfiguration(pluginId).then((config) => {
      config.Enabled = view.querySelector("#Enabled").checked;
      config.BaseUrl = view.querySelector("#BaseUrl").value.trim().replace(/\/+$/, "");
      config.Username = view.querySelector("#Username").value.trim();
      config.Password = view.querySelector("#Password").value;
      config.ArchiveDays = parseInt(view.querySelector("#ArchiveDays").value, 10) || 7;
      config.CacheMinutes = parseInt(view.querySelector("#CacheMinutes").value, 10) || 30;
      config.MetadataLanguage = view.querySelector("#MetadataLanguage").value.trim() || "he-IL";
      config.TmdbBearerToken = view.querySelector("#TmdbBearerToken").value.trim();
      config.ShowMovies = view.querySelector("#ShowMovies").checked;
      config.ShowSeries = view.querySelector("#ShowSeries").checked;
      config.ShowPrograms = view.querySelector("#ShowPrograms").checked;

      ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
        Dashboard.processPluginConfigurationUpdateResult(result);
      });
    });

    return false;
  });
}
