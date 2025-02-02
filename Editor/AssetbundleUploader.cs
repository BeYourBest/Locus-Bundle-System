﻿using System.IO;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace BundleSystem
{
    public class AssetbundleUploader : MonoBehaviour
    {
        public static void UploadAllRemoteFiles(AssetbundleBuildSettings settings)
        {
            System.Exception exception = null;
            try
            {
                var buildTargetString = EditorUserBuildSettings.activeBuildTarget.ToString();
                var credential = new NetworkCredential(settings.FtpUserName, settings.FtpUserPass);
                var uploadRootPath = Utility.CombinePath(settings.FtpHost, buildTargetString);
                var dirInfo = new DirectoryInfo(Utility.CombinePath(settings.RemoteOutputPath, buildTargetString));
                var files = dirInfo.GetFiles();
                var progress = 0f;
                var progressStep = 1f / files.Length;
                foreach (var fileInfo in dirInfo.GetFiles())
                {
                    byte[] data = File.ReadAllBytes(fileInfo.FullName);
                    EditorUtility.DisplayProgressBar("Uploading AssetBundles", fileInfo.Name, progress);
                    FtpUpload(Utility.CombinePath(uploadRootPath, fileInfo.Name), data, credential);
                    progress += progressStep;
                }
            }
            catch (System.Exception e)
            {
                exception = e;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (exception == null)
            {
                EditorUtility.DisplayDialog("Upload Success", "Uploaded All AssetBundles", "Confirm");
            }
            else
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Upload Failed", "Got Error while uploading see console for detail.", "Confirm");
            }
        }

        static void FtpUpload(string path, byte[] data, NetworkCredential credential)
        {
            FtpWebRequest req = (FtpWebRequest)WebRequest.Create(path);
            req.Method = WebRequestMethods.Ftp.UploadFile;
            req.Credentials = credential;

            req.ContentLength = data.Length;
            using (Stream reqStream = req.GetRequestStream())
            {
                reqStream.Write(data, 0, data.Length);
            }

            using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
            {
                Debug.Log($"Got upload web response, status: {resp.StatusCode}");
                if (resp.StatusCode != FtpStatusCode.ClosingControl && resp.StatusCode != FtpStatusCode.ClosingData)
                {
                    throw new System.Exception($"File Upload Failed to {path}, Code : {resp.StatusCode}");
                }
            }
        }
    }
}