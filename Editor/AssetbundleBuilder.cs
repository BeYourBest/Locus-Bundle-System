﻿using System.Linq;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.WriteTypes;
using UnityEngine;
using System;

namespace BundleSystem
{
    public enum BuildType
    {
        Remote,
        Local
    }

    /// <summary>
    /// class that contains actual build functionalities
    /// </summary>
    public static class AssetbundleBuilder
    {
        const string LogFileName = "BundleBuildLog.txt";
        const string LogExpectedSharedBundleFileName = "ExpectedSharedBundles.txt";

        class CustomBuildParameters : BundleBuildParameters
        {
            public AssetbundleBuildSettings CurrentSettings;
            public BuildType CurrentBuildType;
            public Dictionary<string, List<string>> DependencyDic;

            public CustomBuildParameters(AssetbundleBuildSettings settings, 
                BuildTarget target, 
                BuildTargetGroup group, 
                string outputFolder,
                Dictionary<string, List<string>> deps,
                BuildType  buildType) : base(target, group, outputFolder)
            {
                CurrentSettings = settings;
                CurrentBuildType = buildType;
                DependencyDic = deps;
            }

            // Override the GetCompressionForIdentifier method with new logic
            public override BuildCompression GetCompressionForIdentifier(string identifier)
            {
                //local bundles are always lz4 for faster initializing
                if (CurrentBuildType == BuildType.Local) return BuildCompression.LZ4;

                //find user set compression method
                var found = CurrentSettings.BundleSettings.FirstOrDefault(setting => setting.BundleName == identifier);
                return found == null || !found.CompressBundle ? BuildCompression.LZ4 : BuildCompression.LZMA;
            }
        }

        public static void BuildAssetBundles(BuildType buildType)
        {
            var editorInstance = AssetbundleBuildSettings.EditorInstance;
            BuildAssetBundles(editorInstance, buildType);
        }

        public static void WriteExpectedSharedBundles(AssetbundleBuildSettings settings)
        {
            if(!Application.isBatchMode)
            {
                //have to ask save current scene
                var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

                if(!saved) 
                {
                    EditorUtility.DisplayDialog("Failed!", $"User Canceled", "Confirm");
                    return;
                }
            }
            
            var tempPrevSceneKey = "WriteExpectedSharedBundlesPrevScene";
            var prevScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            EditorPrefs.SetString(tempPrevSceneKey, prevScene.path);
            
            var localBundles = new HashSet<string>();
            foreach(var bundle in settings.BundleSettings)
            {
                if(bundle.IncludedInPlayer) localBundles.Add(bundle.BundleName);
            }

            var bundleList = GetAssetBundlesList(settings);
                
            var treeResult = AssetDependencyTree.ProcessDependencyTree(bundleList, new SharedBundleSetting(
                    localBundles, 
                    settings.AutoCreateSharedBundles,
                    settings.FolderBasedSharedBundle));

            WriteSharedBundleLog($"{Application.dataPath}/../", treeResult);
            if(!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog("Succeeded!", $"Check {LogExpectedSharedBundleFileName} in your project root directory!", "Confirm");
            }

            //domain reloaded, we need to restore previous scene path
            var prevScenePath = EditorPrefs.GetString(tempPrevSceneKey, string.Empty);
            //back to previous scene as all processed scene's prefabs are unpacked.
            if(string.IsNullOrEmpty(prevScenePath))
            {
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects);
            }
            else
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(prevScenePath);
            }
        }

        public static List<AssetBundleBuild> GetAssetBundlesList(AssetbundleBuildSettings settings)
        {
            var bundleList = new List<AssetBundleBuild>();

            foreach (var setting in settings.BundleSettings)
            {
                // if( !setting.IncludeInBuild ) 
                // {
                //     Debug.Log($"Skipping excluded bundle '{setting.BundleName}'");
                //     continue;
                // } 
                
                //find folder
                var folderPath = AssetDatabase.GUIDToAssetPath(setting.Folder.guid);
                if (!AssetDatabase.IsValidFolder(folderPath)) throw new Exception($"Could not found Path {folderPath} for {setting.BundleName}");

                //collect assets
                var assetPathes = new List<string>();
                var loadPathes = new List<string>();
                Utility.GetFilesInDirectory(string.Empty, assetPathes, loadPathes, folderPath, setting.IncludeSubfolder);
                if (assetPathes.Count == 0) Debug.LogWarning($"Could not found Any Assets {folderPath} for {setting.BundleName}");

                //make assetbundlebuild
                var newBundle = new AssetBundleBuild();
                newBundle.assetBundleName = setting.BundleName;
                newBundle.assetNames = assetPathes.ToArray();
                newBundle.addressableNames = loadPathes.ToArray();
                bundleList.Add(newBundle);
            }

            return bundleList;
        }

        public static void BuildAssetBundles(AssetbundleBuildSettings settings, BuildType buildType)
        {
            if(!Application.isBatchMode)
            {
                //have to ask save current scene
                var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

                if(!saved) 
                {
                    EditorUtility.DisplayDialog("Build Failed!", $"User Canceled", "Confirm");
                    return;
                }
            }

            var bundleList = GetAssetBundlesList(settings);

            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var groupTarget = BuildPipeline.GetBuildTargetGroup(buildTarget);

            var outputPath = Utility.CombinePath(buildType == BuildType.Local ? settings.LocalOutputPath : settings.RemoteOutputPath, buildTarget.ToString());

            var localBundles = new HashSet<string>();
            foreach(var bundle in settings.BundleSettings)
            {
                if(bundle.IncludedInPlayer) localBundles.Add(bundle.BundleName);
            }

            var treeResult = AssetDependencyTree.ProcessDependencyTree(bundleList, new SharedBundleSetting(
                    localBundles, 
                    settings.AutoCreateSharedBundles,
                    settings.FolderBasedSharedBundle));
                    
            var buildParams = new CustomBuildParameters(settings, buildTarget, groupTarget, outputPath, treeResult.BundleDependencies, buildType);

            buildParams.UseCache = !settings.ForceRebuild;

            if (buildParams.UseCache && settings.UseCacheServer)
            {
                buildParams.CacheServerHost = settings.CacheServerHost;
                buildParams.CacheServerPort = settings.CacheServerPort;
            }

            ContentPipeline.BuildCallbacks.PostPackingCallback += PostPackingForSelectiveBuild;
            var returnCode = ContentPipeline.BuildAssetBundles(buildParams, new BundleBuildContent(treeResult.Results), out var results);
            ContentPipeline.BuildCallbacks.PostPackingCallback -= PostPackingForSelectiveBuild;

            if (returnCode == ReturnCode.Success)
            {
                //only remote bundle build generates link.xml
                switch(buildType)
                {
                    case BuildType.Local:
                        WriteManifestFile(outputPath, results, buildTarget, settings.RemoteURL, settings.AccessToken);
                        WriteLogFile(outputPath, results);
                        if(!Application.isBatchMode) EditorUtility.DisplayDialog("Build Succeeded!", "Local bundle build succeeded!", "Confirm");
                        break;
                    case BuildType.Remote:
                        WriteManifestFile(outputPath, results, buildTarget, settings.RemoteURL, settings.AccessToken);
                        WriteLogFile(outputPath, results);
                        var linkPath = TypeLinkerGenerator.Generate(settings, results);
                        if (!Application.isBatchMode) EditorUtility.DisplayDialog("Build Succeeded!", $"Remote bundle build succeeded, \n {linkPath} updated!", "Confirm");
                        break;
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Build Failed!", $"Bundle build failed, \n Code : {returnCode}", "Confirm");
                Debug.LogError(returnCode);
            }
        }

        private static ReturnCode PostPackingForSelectiveBuild(IBuildParameters buildParams, IDependencyData dependencyData, IWriteData writeData)
        {
            var customBuildParams = buildParams as CustomBuildParameters;
            var depsDic = customBuildParams.DependencyDic;

            List<string> includedBundles;

            if(customBuildParams.CurrentBuildType == BuildType.Local)
            {
                //deps includes every local dependencies recursively
                includedBundles = customBuildParams.CurrentSettings.BundleSettings
                    .Where(setting => setting.IncludedInPlayer)
                    .Select(setting => setting.BundleName)
                    .SelectMany(bundleName => Utility.CollectBundleDependencies(depsDic, bundleName, true))
                    .Distinct()
                    .ToList();
            }
            //if not local build, we include everything
            else
            {
                includedBundles = depsDic.Keys.ToList();
            }

            //quick exit 
            if (includedBundles == null || includedBundles.Count == 0)
            {
                Debug.Log("Nothing to build");
                writeData.WriteOperations.Clear();
                return ReturnCode.Success;
            }

            for (int i = writeData.WriteOperations.Count - 1; i >= 0; --i)
            {
                string bundleName;
                switch (writeData.WriteOperations[i])
                {
                    case SceneBundleWriteOperation sceneOperation:
                        bundleName = sceneOperation.Info.bundleName;
                        break;
                    case SceneDataWriteOperation sceneDataOperation:
                        var bundleWriteData = writeData as IBundleWriteData;
                        bundleName = bundleWriteData.FileToBundle[sceneDataOperation.Command.internalName];
                        break;
                    case AssetBundleWriteOperation assetBundleOperation:
                        bundleName = assetBundleOperation.Info.bundleName;
                        break;
                    default:
                        Debug.LogError("Unexpected write operation");
                        return ReturnCode.Error;
                }

                // if we do not want to build that bundle, remove the write operation from the list
                if (!includedBundles.Contains(bundleName))
                {
                    writeData.WriteOperations.RemoveAt(i);
                }
            }

            return ReturnCode.Success;
        }

        /// <summary>
        /// write manifest into target path.
        /// </summary>
        static void WriteManifestFile(string path, IBundleBuildResults bundleResults, BuildTarget target, string remoteURL, string AccessToken)
        {
            var manifest = new AssetbundleBuildManifest();
            manifest.BuildTarget = target.ToString();

            //we use unity provided dependency result for final check
            var deps = bundleResults.BundleInfos.ToDictionary(kv => kv.Key, kv => kv.Value.Dependencies.ToList());

            foreach (var result in bundleResults.BundleInfos)
            {
                var bundleInfo = new AssetbundleBuildManifest.BundleInfo();
                bundleInfo.BundleName = result.Key;
                bundleInfo.Dependencies = Utility.CollectBundleDependencies(deps, result.Key);
                bundleInfo.Hash = result.Value.Hash;
                bundleInfo.Size = new FileInfo(result.Value.FileName).Length;
                manifest.BundleInfos.Add(bundleInfo);
            }

            //sort by size
            manifest.BundleInfos.Sort((a, b) => b.Size.CompareTo(a.Size));
            var manifestString = JsonUtility.ToJson(manifest);
            manifest.GlobalHash = Hash128.Compute(manifestString);
            manifest.BuildTime = DateTime.UtcNow.Ticks;
            manifest.RemoteURL = remoteURL;
            manifest.AccessToken = AccessToken;

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            File.WriteAllText(Utility.CombinePath(path, AssetbundleBuildSettings.ManifestFileName), JsonUtility.ToJson(manifest, true));
        }

        static void WriteSharedBundleLog(string path, AssetDependencyTree.ProcessResult treeResult)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Build Time : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine($"Possible shared bundles will be created..");
            sb.AppendLine();

            foreach(var grp in treeResult.SharedNodes.GroupBy(node => node.BundleName))
            {
                sb.AppendLine($"SharedBundle - {grp.Key} is referenced by");
                foreach(var node in grp) 
                {
                    foreach(var refitem in node.GetReferencedBy()) sb.AppendLine($"    - {node.Path} <- {refitem.Path}({refitem.BundleName})");
                }
                sb.AppendLine();
            }

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            File.WriteAllText(Utility.CombinePath(path, LogExpectedSharedBundleFileName), sb.ToString());
        }


        /// <summary>
        /// write logs into target path.
        /// </summary>
        static void WriteLogFile(string path, IBundleBuildResults bundleResults)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Build Time : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine();

            for (int i = 0; i < bundleResults.BundleInfos.Count; i++)
            {
                var bundleInfo = bundleResults.BundleInfos.ElementAt(i);
                var writeResult = bundleResults.WriteResults.ElementAt(i);
                sb.AppendLine($"----File Path : {bundleInfo.Value.FileName}----");
                var assetDic = new Dictionary<string, ulong>();
                foreach(var file in writeResult.Value.serializedObjects)
                {
                    //skip nonassettype
                    if (file.serializedObject.fileType == UnityEditor.Build.Content.FileType.NonAssetType) continue;

                    //gather size
                    var assetPath = AssetDatabase.GUIDToAssetPath(file.serializedObject.guid.ToString());
                    if (!assetDic.ContainsKey(assetPath))
                    {
                        assetDic.Add(assetPath, file.header.size);
                    } 
                    else assetDic[assetPath] += file.header.size;
                }

                //sort by it's size
                var sortedAssets = assetDic.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key);

                foreach(var asset in sortedAssets)
                {
                    sb.AppendLine($"{(asset.Value * 0.000001f).ToString("0.00000").PadLeft(10)} mb - {asset.Key}");
                }

                sb.AppendLine();
            }

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            File.WriteAllText(Utility.CombinePath(path, LogFileName), sb.ToString());
        }

        public class SharedBundleSetting : ISharedBundleSetting
        {
            public SharedBundleSetting(HashSet<string> localBundles, bool enableSharedBundle, bool folderBased)
            {
                LocalBundles = localBundles;
                EnableSharedBundle = enableSharedBundle;
                FolderBased = folderBased;
            }

            public HashSet<string> LocalBundles { get; private set; }
            public bool EnableSharedBundle { get; private set; }
            public bool FolderBased { get; private set; }
            public bool IsLocalBundle(string bundleName) => LocalBundles.Contains(bundleName);
            public bool AllowSharedBundle(string assetPath) => EnableSharedBundle;

            public string GetSharedBundleName(string assetPath, bool isLocal)
            {
                if(FolderBased)
                {
                    var bundleName = System.IO.Path.GetDirectoryName(assetPath).Replace('/', '_').Replace('\\', '_');
                    return bundleName + (isLocal? "_Local" : string.Empty);
                }
                else
                {
                    return AssetDatabase.AssetPathToGUID(assetPath);
                }
            }
        }
    }
}
