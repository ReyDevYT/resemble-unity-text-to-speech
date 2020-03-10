using System.IO;
using UnityEngine;
using UnityEditor;
using Resemble;
using Resemble.GUIEditor;
using Resemble.Structs;
using Resources = Resemble.Resources;

[System.Serializable]
public class AsyncRequest
{
    private const string tempFileName = "UnityProject - Temp ";
    private const float checkCooldown = 1.5f;       //Time in seconds between each request to know if the clip is ready to be downloaded.
    private const int waitClipTimout = 600;         //Maximum time a clip can take to be generated, after it's considered a timout.

    private static bool refreshing;
    public Task currentTask;
    public string saveDirectory;
    public string fileName;
    public bool deleteClipAtEnd;
    public string requestName;
    public string clipUUID;
    public Object notificationLink;
    public bool needToBeRefresh;
    public double lastCheckTime;
    public Error error;
    public Status status;

    public float downloadProgress
    {
        get
        {
            if (status != Status.Downloading)
                return 0.0f;
            return currentTask.preview.download.progress;
        }
    }
    public bool isDone
    {
        get
        {
            switch (status)
            {
                case Status.Completed:
                case Status.Error:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary> Build a async request for a clip. This request handles patching, downloading and notifications. </summary>
    public static AsyncRequest Make(Clip clip)
    {
        //Build request
        AsyncRequest request = new AsyncRequest();
        request.status = Status.BuildRequest;
        string savePath = clip.GetSavePath();
        request.saveDirectory = Path.GetDirectoryName(savePath);
        request.fileName = Path.GetFileName(savePath);
        request.requestName = clip.speech.name + " > " + clip.clipName;
        request.clipUUID = clip.uuid;
        request.notificationLink = clip;

        //Generate place holder
        request.status = Status.GeneratePlaceHolder;
        clip.clip = request.GeneratePlaceHolder();

        //No UUID - Create new clip
        request.status = Status.SendDataToAPI;
        if (string.IsNullOrEmpty(request.clipUUID))
        {
            //Create new clip
            CreateClipData data = new CreateClipData(clip.clipName, clip.text.BuildResembleString(), clip.speech.voiceUUID);
            request.currentTask = APIBridge.CreateClip(data, (ClipStatus status, Error error) =>
            { request.clipUUID = clip.uuid = status.id;  RegisterRequestToPool(request); });

        }
        else
        {
            //Patch existing clip
            ClipPatch patch = new ClipPatch(clip.clipName, clip.text.BuildResembleString(), clip.speech.voiceUUID);
            request.currentTask = APIBridge.UpdateClip(request.clipUUID, patch, (string content, Error error) =>
            { RegisterRequestToPool(request); });
        }

        //Return the request
        return request;
    }

    /// <summary> Build a async request for a one-shot clip. This request handles creation, downloading, notifications and deletion. </summary>
    public static AsyncRequest Make(string body, string voice, string savePath)
    {
        //Build request
        AsyncRequest request = new AsyncRequest();
        request.status = Status.BuildRequest;
        request.saveDirectory = Path.GetDirectoryName(savePath);
        request.fileName = Path.GetFileName(savePath);
        request.deleteClipAtEnd = true;
        request.requestName = "OneShot > " + request.fileName.Remove(request.fileName.Length - 4);

        //Generate placeholder
        request.status = Status.GeneratePlaceHolder;
        request.GeneratePlaceHolder();

        //Send request
        request.status = Status.SendDataToAPI;
        CreateClipData data = new CreateClipData(GetTemporaryName(), body, voice);
        request.currentTask = APIBridge.CreateClip(data, (ClipStatus status, Error error) =>
        { request.clipUUID = status.id; RegisterRequestToPool(request);});

        //Return the request
        return request;
    }

    /// <summary> Returns the current request running on the given UUID if it exists. </summary>
    public static AsyncRequest Get(string clipUUID)
    {
        for (int i = 0; i < Resources.instance.requests.Count; i++)
        {
            AsyncRequest request = Resources.instance.requests[i];
            if (request.clipUUID == clipUUID)
                return request;
        }
        return null;
    }

    /// <summary> Add a request to the pool. </summary>
    private static void RegisterRequestToPool(AsyncRequest request)
    {
        request.status = Status.NeedNewClipStatusRequest;
        Resources.instance.requests.Add(request);
        EditorUtility.SetDirty(Resources.instance);
        if (!refreshing)
        {
            EditorApplication.update += ExecutePoolRequests;
            refreshing = true;
        }
    }

    [InitializeOnLoadMethod]
    private static void RegisterRefreshEvent()
    {
        if (Resources.instance.requests.Count == 0)
            return;
        refreshing = true;
        EditorApplication.update += ExecutePoolRequests;
    }

    private static void DisposeRefreshEvent()
    {
        refreshing = false;
        EditorApplication.update -= ExecutePoolRequests;
    }

    private static void ExecutePoolRequests()
    {
        double time = EditorApplication.timeSinceStartup;
        int count = Resources.instance.requests.Count;
        for (int i = 0; i < count; i++)
        {
            AsyncRequest request = Resources.instance.requests[i];
            switch (request.status)
            {
                case Status.NeedNewClipStatusRequest:
                case Status.WaitClipStatusRequest:
                    ExecuteRequest(time, request);
                    break;
                case Status.Completed:
                case Status.Error:
                    Resources.instance.requests.RemoveAt(i);
                    EditorUtility.SetDirty(Resources.instance);
                    i--;
                    count--;
                    continue;
                default:
                    break;
            }
            ExecuteRequest(time, Resources.instance.requests[i]);
        }

        if (Resources.instance.requests.Count == 0)
            DisposeRefreshEvent();
    }

    private static void ExecuteRequest(double time, AsyncRequest request)
    {
        //Do nothing if request is waiting for API response
        double delta = time - request.lastCheckTime;
        if (request.status == Status.WaitClipStatusRequest)
        {
            //Check timout error (the API take too much time to generate the clip)
            if (delta > waitClipTimout)
                request.SetError(Error.Timeout);
            return;
        }

        //The next steps are only used to send status request
        if (request.status != Status.NeedNewClipStatusRequest)
            return;

        //Force a delay in requests to avoid flooding the api
        if (delta < 0.0f || delta > checkCooldown)
            request.lastCheckTime = time;
        else
            return;

        //Send GetClip request
        request.currentTask = APIBridge.GetClip(request.clipUUID, (ResembleClip clip, Error error) =>
        {
            //Error
            if (error)
                request.SetError(error);

            //Receive an response
            else
            {
                //Clip is ready - Start downloading
                if (clip.finished)
                    DownloadClip(request, clip.link);

                //Clip is not ready - Mark to create a request next time
                else
                    request.status = Status.NeedNewClipStatusRequest;
            }
        });
        request.status = Status.WaitClipStatusRequest;
    }

    private static void DownloadClip(AsyncRequest request, string url)
    {
        request.status = Status.Downloading;
        request.currentTask = APIBridge.DownloadClip(url, (byte[] data, Error error) => 
        { OnDownloaded(request, data, error); });
    }

    private static void OnDownloaded(AsyncRequest request, byte[] data, Error error)
    {
        //Handle error
        if (error)
        {
            request.SetError(error);
            return;
        }

        //Download completed
        else
        {
            request.status = Status.WritingAsset;
        }

        //Write file
        string savePath = request.saveDirectory + "/" + request.fileName;
        File.WriteAllBytes(savePath, data);

        //Import asset
        savePath = Utils.LocalPath(savePath);
        AssetDatabase.ImportAsset(savePath, ImportAssetOptions.ForceUpdate);
        if (request.notificationLink == null)
            request.notificationLink = AssetDatabase.LoadAssetAtPath<AudioClip>(savePath);

        //Send notification
        NotificationsPopup.Add("Download completed\n" + request.requestName, MessageType.Info, request.notificationLink);

        //Delete clip if needed
        if (!request.deleteClipAtEnd)
        {
            request.status = Status.Completed;
        }
        else
        {
            request.status = Status.SendRequestDeletion;
            request.currentTask = APIBridge.DeleteClip(request.clipUUID, (string content, Error deleteError) => 
            {
                if (deleteError)
                    request.SetError(deleteError);
                else
                    request.status = Status.Completed;
            });
        }
    }

    private static string GetTemporaryName()
    {
        return string.Concat(tempFileName, System.DateTime.UtcNow.ToBinary().ToString());
    }

    private static bool ParseTemporaryName(string name, out System.DateTime time)
    {
        time = new System.DateTime();

        if (!name.StartsWith(tempFileName))
            return false;

        string date = name.Remove(0, tempFileName.Length);
        long timeStamp;
        if (!long.TryParse(date, out timeStamp))
            return false;

        time = System.DateTime.FromBinary(timeStamp);
        return true;
    }

    /// <summary> Generate a placeHolder wav file at saveDirectory and return it. </summary>
    public AudioClip GeneratePlaceHolder()
    {
        //Get and create directory
        string savePath = saveDirectory + "/" + fileName;
        Directory.CreateDirectory(saveDirectory);

        //Copy placeholder file
        byte[] file = File.ReadAllBytes(AssetDatabase.GetAssetPath(Resources.instance.processClip));
        File.WriteAllBytes(savePath, file);

        //Import placeholder
        savePath = Utils.LocalPath(savePath);
        AssetDatabase.ImportAsset(savePath, ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<AudioClip>(savePath);
    }

    /// <summary> Marks the request with the error status. The request will be removed from the pool. </summary>
    public void SetError(Error error)
    {
        this.error = error;
        if (error)
        {
            status = Status.Error;
            NotificationsPopup.Add(error.ToString(), MessageType.Error, notificationLink);
        }
    }

    public enum Status
    {
        BuildRequest,
        GeneratePlaceHolder,
        SendDataToAPI,
        NeedNewClipStatusRequest,
        WaitClipStatusRequest,
        Downloading,
        WritingAsset,
        SendRequestDeletion,
        Completed,
        Error,
    }

}
