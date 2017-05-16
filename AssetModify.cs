using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace AssetSwitcherDLL
{
    public class AssetModify
    {
        delegate void AssetHandler(string file);

        public static string[] AsssetExtension = { ".asset", ".prefab", ".unity" };

        List<Assembly> _assemblies;
        DirectoryInfo _metadir;
        DirectoryInfo _assetdir;

        //all .cs.meta files Key:filename(has extension) Value:fullpath
        ConcurrentDictionary<string, string> _csmetafiles = new ConcurrentDictionary<string, string>();
        //has any AsssetExtension files
        ConcurrentBag<string> _assetfiles = new ConcurrentBag<string>();


        //Assembly corresponding guid
        Dictionary<Assembly, string> _assembliesguid = new Dictionary<Assembly, string>();

        //C# Type <=> FilePath
        //对于某种类他总是应该存在对应的文件[文件名与类名应该一致]
        ConcurrentDictionary<Type, string> _type2path = new ConcurrentDictionary<Type, string>();
        ConcurrentDictionary<string, Type> _path2type = new ConcurrentDictionary<string, Type>();

        ConcurrentDictionary<Type, string> _type2fileid = new ConcurrentDictionary<Type, string>();

        ConcurrentDictionary<string, string> _guid2path = new ConcurrentDictionary<string, string>();

        ConcurrentDictionary<string, AssetHandler> _extension_handler = new ConcurrentDictionary<string, AssetHandler>();

        //note assembly should be in assetdir[has corresponding .meta]
        public AssetModify(List<Assembly> assemblies, DirectoryInfo metadir, DirectoryInfo assetdir)
        {
            if (assemblies == null)
                throw new ArgumentNullException("assembly");
            if (metadir == null)
                throw new ArgumentNullException("metadir");
            if (assetdir == null)
                assetdir = metadir;

            _assemblies = assemblies;
            _metadir = metadir;
            _assetdir = assetdir;

            foreach (var extension in AsssetExtension)
            {
                _extension_handler.TryAdd(extension, DefaultAssetModfiy);
            }
        }

        public void RegisterExtensionHandler(string extension, Action<string> action)
        {
            if (_extension_handler.ContainsKey(extension))
                _extension_handler[extension] +=new AssetHandler(action);
        }

        string ShortPathName(DirectoryInfo parent, string fullpath)
        {
            return fullpath.Replace(parent.FullName, "");
        }

        public void BuildAssemblyInfo(string pattern = ".*")
        {
            var assembliesname = _assemblies.Select((assembly) => assembly.GetName().Name + ".dll.meta");
            var assembliesmeta = new ConcurrentDictionary<string, string>();

            //find assemblyname.meta build _assetfiles
            var assetdirfileinfos = _assetdir.GetFiles("*.*", SearchOption.AllDirectories);
            Parallel.ForEach(assetdirfileinfos, file =>
            {
                if (file.Extension == ".meta" && file.Name.Contains(".dll"))
                {
                    var assemblyname = assembliesname.FirstOrDefault(name => file.Name == name);
                    if (assemblyname != null)
                        assembliesmeta.TryAdd(assemblyname, file.FullName);
                }

                var regex = new Regex(pattern);
                if (!regex.IsMatch(file.FullName))
                    return;

                if (AsssetExtension.Contains(file.Extension))
                {
                    _assetfiles.Add(file.FullName);
                    return;
                }
            });


            //build _assembliesguid;
            foreach (var assembly in _assemblies)
            {
                var assemblyname = assembly.GetName().Name + ".dll.meta";
                var assemblymeta = string.Empty;
                if (!assembliesmeta.TryGetValue(assemblyname, out assemblymeta))
                {
                    Console.WriteLine(string.Format("build assmebliesguid: Try Get {0} 'meta failed", assemblyname));
                    continue;
                }

                try
                {
                    var yaml = new YamlDotNet.Serialization.Deserializer().Deserialize(File.OpenText(assemblymeta));
                    //var json = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build().Serialize(yaml);

                    var pairs = (Dictionary<object, object>)yaml;
                    _assembliesguid[assembly] = pairs["guid"].ToString();
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("build assembliesguid parse {0} failed Exception: {1}", assemblymeta, e));
                }
            }

            //build _metafiles
            var metadirfileinfos = _metadir.GetFiles("*.*", SearchOption.AllDirectories);
            Parallel.ForEach(metadirfileinfos, file =>
              {
                  if (file.Extension != ".meta")
                      return;
                  if (!file.Name.Contains(".cs"))
                      return;
                  _csmetafiles.AddOrUpdate(file.Name, file.FullName, (key, value) =>
                  {
                      Console.WriteLine(string.Format("{0} has exist please change filename&classname.warning replace {1} by {2}",
                          file.Name,
                          ShortPathName(_metadir, value),
                          ShortPathName(_metadir, file.FullName)));
                      return file.FullName;
                  });
              }
             );

            //parse metafiles build _guid2path
            Parallel.ForEach(_csmetafiles, pair =>
             {
                 try
                 {
                     var yaml = new YamlDotNet.Serialization.Deserializer().Deserialize(File.OpenText(pair.Value));
                     var pairs = (Dictionary<object, object>)yaml;
                     if (!_guid2path.TryAdd(pairs["guid"].ToString(), pair.Value))
                         Console.WriteLine(string.Format("build guidpath add {0},{1} failed", pairs["guid"], pair.Value));
                 }
                 catch (Exception e)
                 {
                     Console.WriteLine(string.Format("build guidpath parse {0} failed Exception: {1}", pair.Value, e));
                 }
             });

            //parse assembly build _type2path & _path2type
            var types = _assemblies
                .Select(assembly => assembly.GetTypes())
                .SelectMany(t => t)
                //必须继承ScriptableObject才能序列化
                //必须继承MonoBehaviour才能被挂接
                .Where(type => type.IsSubclassOf(typeof(UnityEngine.ScriptableObject)) || type.IsSubclassOf(typeof(UnityEngine.MonoBehaviour))).ToList();
            Parallel.ForEach(types, type =>
             {
                 var fullpath = string.Empty;
                 if (!_csmetafiles.TryGetValue(type.Name + ".cs.meta", out fullpath))
                 {
                     Console.WriteLine(string.Format("warning :find {0}.cs failed", type.Name));
                     return;
                 }
                 if (!_type2path.TryAdd(type, fullpath))
                 {
                     Console.WriteLine(string.Format("warning :type {0} has existed", type.Name));
                     return;
                 }
                 if (!_path2type.TryAdd(fullpath, type))
                     Console.WriteLine(string.Format("error:type {0} path {1} existed!", type.Name, ShortPathName(_metadir, fullpath)));

                 if (!_type2fileid.TryAdd(type, FileIDUtil.Compute(type).ToString()))
                     Console.WriteLine(string.Format("error:type {0} add fileid failed", type.Name));
             });
        }

        public void ModifyAsset()
        {
            Parallel.ForEach(_assetfiles, file =>
            {
                var extension = Path.GetExtension(file);
                AssetHandler handler = null;
                if (_extension_handler.TryGetValue(extension, out handler))
                    handler.Invoke(file);
            });
        }

        public void ModfiyAsset(string file)
        {
            var extension = Path.GetExtension(file);
            AssetHandler handler = null;
            if (_extension_handler.TryGetValue(extension, out handler))
                return;
            Parallel.ForEach(_assetfiles, asset =>
            {
                var filename = Path.GetFileName(asset);
                if (filename != file)
                    return;
                handler.Invoke(file);
            });
        }

        public void DefaultAssetModfiy(string file)
        {
            var shortfilename = ShortPathName(_assetdir, file);
            try
            {
                var lines = File.ReadAllLines(file);
                lines = lines.Select(line =>
                {
                    if (!line.Contains("m_Script"))
                        return line;
                    var begin = line.IndexOf("fileID: ") + 8;
                    if (begin == -1)
                        return line;
                    var end = line.IndexOf(',', begin);
                    if (end == -1)
                        return line;

                    var fileID = line.Substring(begin, end - begin);
                    begin = line.IndexOf("guid: ") + 6;
                    if (begin == -1)
                        return line;
                    end = begin + 32;
                    var guid = line.Substring(begin, end - begin);

                    if (_assembliesguid.ContainsValue(guid))
                    {
                        Console.WriteLine(string.Format("{0}.MonoBehavior.m_Script Had Modfiy Skip it", shortfilename));
                        return line;
                    }

                    //guid->path
                    string cspath = null;
                    if (!_guid2path.TryGetValue(guid, out cspath))
                    {
                        Console.WriteLine(string.Format("AssetModfiy.Map GUID {0} To Path Failed", shortfilename));
                        return line;
                    }

                    Console.WriteLine(string.Format("Old {0}.MonoBehavior.m_Script {{fileID:{1} guid:{2}}}", shortfilename, fileID, guid));

                    //path->type
                    Type type = null;
                    if (!_path2type.TryGetValue(cspath, out type))
                    {
                        Console.WriteLine(string.Format("AssetModfiy.Map(GUID:{1}) Path {0} To Type Failed", shortfilename, guid));
                        return line;
                    }

                    string newguid = null;
                    //type->assembly->guid
                    if (!_assembliesguid.TryGetValue(type.Assembly, out newguid))
                    {
                        Console.WriteLine(string.Format("Error: AssetModfiy.Map Type {0} To GUID", type));
                        return line;
                    }
                    string newfileID = null;
                    //type->fileID
                    if (!_type2fileid.TryGetValue(type, out newfileID))
                    {
                        Console.WriteLine(string.Format("Error: AssetModfiy.Map Type {0} To fileID", type));
                        return line;
                    }

                    Console.WriteLine(string.Format("New {0}.MonoBehavior.m_Script {{fileID:{1} guid:{2}}}", shortfilename, newfileID, newguid));
                    return line.Replace(fileID, newfileID).Replace(guid, newguid);
                }).ToArray();

                var modfiy = file + ".modfiy";
                if (!File.Exists(modfiy))
                    File.Copy(file, modfiy);

                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);

                //行拼接
                //并干掉: ''

                //强制覆盖
                File.Delete(file);
                File.WriteAllLines(file, lines);
            }
            catch (Exception e)
            {
                Console.WriteLine(string.Format("AssetModfiy.ModfiyAsset {0} Failed Exception:{1}", shortfilename, e));
            }
        }

        public void AssetBundleModfiy(string file)
        {

        }


        public void RevertAsset()
        {
            var assetdirfileinfos = _assetdir.GetFiles("*.modfiy", SearchOption.AllDirectories);
            Parallel.ForEach(assetdirfileinfos, file =>
            {
                var targetfile = file.FullName.Replace(file.Extension, "");
                if (File.Exists(targetfile))
                {
                    File.SetAttributes(targetfile, File.GetAttributes(targetfile) & ~FileAttributes.ReadOnly);
                    File.Delete(targetfile);
                }
                File.Move(file.FullName, targetfile);
            });
        }
    }
}
