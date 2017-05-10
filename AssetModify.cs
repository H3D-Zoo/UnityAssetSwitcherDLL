using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace AssetSwitcherDLL
{
    public class AssetModify
    {
        public static string[] AsssetExtension = { ".asset", ".prefab" };

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
        }

        string ShortPathName(DirectoryInfo parent,string fullpath)
        {
            return fullpath.Replace(parent.FullName, "");
        }

        public void BuildAssemblyInfo()
        {
            var assembliesname = _assemblies.Select((assembly) => assembly.GetName().Name + ".dll.meta");
            var assembliesmeta = new ConcurrentDictionary<string, string>();

            //find assemblyname.meta build _assetfiles
            var assetdirfileinfos = _assetdir.GetFiles("*.*", SearchOption.AllDirectories);
            Parallel.ForEach(assetdirfileinfos, file =>
            {
                if (AsssetExtension.Contains(file.Extension))
                {
                    _assetfiles.Add(file.FullName);
                    return;
                }
                if (file.Extension != ".meta" || file.Extension == ".dll")
                    return;
                if (!file.Name.Contains(".dll"))
                    return;
                var assemblyname = assembliesname.FirstOrDefault(name => file.Name == name);
                if (assemblyname != null)
                    assembliesmeta.TryAdd(assemblyname, file.FullName);
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
                          ShortPathName(_metadir,value), 
                          ShortPathName(_metadir,file.FullName)));
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
                //必须继承该类才能序列化
                .Where(type => type.IsSubclassOf(typeof(UnityEngine.ScriptableObject))).ToList();
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
                     Console.WriteLine(string.Format("error:type {0} path {1} existed!", type.Name,ShortPathName(_metadir,fullpath)));

                 if (!_type2fileid.TryAdd(type, FileIDUtil.Compute(type).ToString()))
                     Console.WriteLine(string.Format("error:type {0} add fileid failed", type.Name));
             });
        }

        public void ModifyAsset()
        {
            Parallel.ForEach(_assetfiles, file =>
            {
                var shortfilename = ShortPathName(_assetdir, file);
                try
                {
                    var yamlstream = new YamlStream();
                    yamlstream.Load(new StringReader(File.ReadAllText(file)));
                    var yamldocument = yamlstream.Documents.FirstOrDefault();
                    if(yamldocument == null || yamldocument.RootNode.NodeType != YamlNodeType.Mapping)
                    {
                        Console.WriteLine(string.Format("YamlStream.Documents Check Failed{0}", file));
                        return;
                    }
                   
                    var yamlmapping = (YamlMappingNode)yamldocument.RootNode;

                    YamlNode MonoBehaviourMappingNode = null;
                    if (!yamlmapping.Children.TryGetValue(
                        new YamlScalarNode("MonoBehaviour"), 
                        out MonoBehaviourMappingNode) || MonoBehaviourMappingNode.NodeType != YamlNodeType.Mapping)
                    {
                        //Console.WriteLine(string.Format("Asset.MonoBehaviour Check Failed"));
                        return;
                    }
                    var monobehaviour = (YamlMappingNode)MonoBehaviourMappingNode;
                    YamlNode m_ScriptMappingNode = null;
                    if (!monobehaviour.Children.TryGetValue(
                        new YamlScalarNode("m_Script"),
                        out m_ScriptMappingNode) || m_ScriptMappingNode.NodeType != YamlNodeType.Mapping)
                    {
                        Console.WriteLine(string.Format("MonoBehaviour.m_Script Check Failed"));
                        return;
                    }
                    var mscript = (YamlMappingNode)m_ScriptMappingNode;

                    var guidScalarNode = new YamlScalarNode("guid");
                    var fileIDScalarNode = new YamlScalarNode("fileID");


                    var guid = (mscript.Children[guidScalarNode] as YamlScalarNode).Value;
                    var fileID = (mscript.Children[fileIDScalarNode] as YamlScalarNode).Value;
                    Console.WriteLine(string.Format("Old {0}.MonoBehavior.m_Script {{fileID:{1} guid:{2}}}", shortfilename, fileID, guid));

                    //guid->path
                    string cspath = null;
                    if (!_guid2path.TryGetValue(guid,out cspath))
                    {
                        Console.WriteLine(string.Format("AssetModfiy.Map GUID {0} To Path Failed", shortfilename));
                        return;
                    }

                    //path->type
                    Type type = null;
                    if (!_path2type.TryGetValue(cspath,out type))
                    {
                        Console.WriteLine(string.Format("AssetModfiy.Map(GUID:{1}) Path {0} To Type Failed", shortfilename, guid));
                        return;
                    }

                    //type->assembly->guid
                    if (!_assembliesguid.TryGetValue(type.Assembly,out guid))
                    {
                        Console.WriteLine(string.Format("Error: AssetModfiy.Map Type {0} To GUID",type));
                        return;
                    }
                    //type->fileID
                    if (!_type2fileid.TryGetValue(type,out fileID))
                    {
                        Console.WriteLine(string.Format("Error: AssetModfiy.Map Type {0} To fileID", type));
                        return;
                    }

                    Console.WriteLine(string.Format("New {0}.MonoBehavior.m_Script {{fileID:{1} guid:{2}}}", shortfilename, fileID, guid));

                    (mscript.Children[guidScalarNode] as YamlScalarNode).Value = guid;
                    (mscript.Children[fileIDScalarNode] as YamlScalarNode).Value = fileID;

                    var modfiy = file + ".modfiy";
                    if (!File.Exists(modfiy))
                        File.Copy(file, modfiy);

                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    var test = new StringWriter();
                    yamlstream.Save(test, false);

                    //注意 yaml 保存的结果不符合unity的，进行二次处理
                    var text = test.GetStringBuilder().ToString();
                    var lines = text.Split('\n').ToList();

                    //删除第一行
                    lines.RemoveAt(0);
                    //删除倒数第二行
                    lines.RemoveAt(lines.Count - 2);

                    //插入原文件的前三行
                    var threelines = File.ReadLines(file).GetEnumerator() ;
                    for (var i = 0; i != 3; ++i)
                    {
                        threelines.MoveNext();
                        lines.Insert(i, threelines.Current);
                    }
                    threelines.Dispose();

                    //行拼接
                    //并干掉: ''
                    text = string.Join("\n", lines.Select(line => line.Replace(": ''", ": ")));

                    //强制覆盖
                    File.Delete(file);
                    File.WriteAllText(file, text);
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("AssetModfiy.ModfiyAsset {0} Failed Exception:{1}", shortfilename, e));
                }
            });
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
