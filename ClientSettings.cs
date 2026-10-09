namespace hsclient;

internal interface IClientSettings
{
    string LoadServer();
    string LoadKey();
    void Save(string server, string key);
}

internal sealed class ClientSettings : IClientSettings
{
    public string LoadServer() => ServerStore.Load();
    public string LoadKey() => AuthKeyStore.Load();
    public void Save(string server, string key)
    {
        ServerStore.Save(server);
        AuthKeyStore.Save(key);
    }
}
