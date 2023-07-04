/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AssemblyPatcher;

/// <summary>
/// 使用Roslyn扫描可能的 partial class 文件
/// </summary>
/// <remarks>后续考虑是否也用Roslyn编译，毕竟已经生成所有源码的TypeTree，不继续编译有点浪费</remarks>
public class PartialClassScanner
{
    public string assemblyName { get;private set; }
    public List<string> changedFiles { get; private set; }
    public string[] defines { get; private set; }
    /// <summary>
    /// 所有需要编译的文件列表
    /// </summary>
    public HashSet<string> allFilesNeeded { get; private set; } = new HashSet<string>();
    public Dictionary<string, SyntaxTree> syntaxTrees { get;private set; } = new Dictionary<string, SyntaxTree>();


    CSharpParseOptions _parseOpt;
    /// <summary>
    /// (file, partial class) 映射
    /// </summary>
    Dictionary<string, List<string>> _docToClassesDic = new Dictionary<string, List<string>>();
    /// <summary>
    /// (partial class, file list) 映射
    /// </summary>
    Dictionary<string, List<string>> _classNameToDocsDic = new Dictionary<string, List<string>>();
    object _locker = new object();

    public PartialClassScanner(string assemblyName, List<string> changedFiles, CSharpParseOptions parseOpt)
    {
        this.assemblyName = assemblyName;
        this.changedFiles = changedFiles;
        _parseOpt = parseOpt;
    }

    public void Scan()
    {
        // 首先扫描发生改变的文件，如果其中没有 partial class，则停止扫描
        foreach(var file in changedFiles)
            ScanFile(file);

        if (_docToClassesDic.Count == 0)
            return;

        var csFiles = GetSourceFilesFromCsProj();
        // 开多个线程开始扫描
        foreach(var csFile in csFiles)
        {
            if (changedFiles.Contains(csFile))
                continue;
            ScanFile(csFile);
        }

        // 分层递归（扫描目标是文件，判断标准是未出现过的partial class)
        var newDocs = new HashSet<string>(changedFiles);
        CollectPartialClassFiles(allFilesNeeded, newDocs);
    }

    HashSet<string> _tmpClasses = new HashSet<string>();
    /// <summary>
    /// 递归扫描 PartialClass
    /// </summary>
    void CollectPartialClassFiles(HashSet<string> docVisited, HashSet<string> newDocs)
    {
        _tmpClasses.Clear();
        // 找出当前文档列表内的partial class关联的其它文档，然后递归步进扫描
        foreach(var doc in newDocs)
        {
            docVisited.Add(doc);
            if(_docToClassesDic.TryGetValue(doc, out var classes))
            {
                foreach(var c in classes)
                    _tmpClasses.Add(c);
            }
        }

        newDocs.Clear();
        // 再从class的doc列表内找到所有未访问过的，重新扫描
        foreach(var c in _tmpClasses)
        {
            if(_classNameToDocsDic.TryGetValue(c, out var docs))
            {
                foreach(var d in docs)
                {
                    if(!docVisited.Contains(d))
                        newDocs.Add(d);
                }
            }
        }

        if (newDocs.Count > 0)
            CollectPartialClassFiles(docVisited, newDocs);
    }

    void ScanFile(string filePath)
    {
        // 未编译时没有FullName, 因此只能自己记录当前的层级
        Stack<MemberDeclarationSyntax> currentScope = new Stack<MemberDeclarationSyntax>();

        StringBuilder tmpBuilder = new StringBuilder();

        Action<MemberDeclarationSyntax> updateScope = node =>
        {
            if(currentScope.Count == 0)
            {
                currentScope.Push(node);
                return;
            }

            while(currentScope.Count > 0)
            {
                var top = currentScope.Peek();
                if(node.Span.Start >  top.Span.End)
                {
                    currentScope.Pop();
                }
                else
                    break;
            }
            currentScope.Push(node);
        };

        Func<string> getFullName = () =>
        {
            tmpBuilder.Clear();
            foreach(var node in currentScope)
            {
                string name = string.Empty;
                switch(node)
                {
                    case NamespaceDeclarationSyntax nsDecl:
                        name = nsDecl.Name.ToString();
                        break;
                    case ClassDeclarationSyntax classDecl:
                        name = classDecl.Identifier.Text;
                        break;
                }
                tmpBuilder.Insert(0, name).Insert(name.Length, '.');
            }
            return tmpBuilder.ToString(0, tmpBuilder.Length - 1);
        };

        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), _parseOpt);
        syntaxTrees.Add(filePath, tree);
        var root = tree.GetCompilationUnitRoot();
        foreach(var node in root.DescendantNodes())
        {
            switch(node)
            {
                case NamespaceDeclarationSyntax nsDecl:
                    updateScope(nsDecl);
                    break;
                case ClassDeclarationSyntax classDecl:
                    updateScope(classDecl);
                    if(classDecl.Modifiers.ToString().Contains("partial"))
                    {
                        string fullNameCls = getFullName();
                        UpdatePartialClassData(filePath, fullNameCls);
                    }
                    break;
            }
        }
    }

    void UpdatePartialClassData(string filePath, string classFullName)
    {
        lock (_locker)
        {
            if(!_docToClassesDic.TryGetValue(filePath, out var lstClasses))
            {
                lstClasses = new List<string>();
                _docToClassesDic.Add(filePath, lstClasses);
            }
            lstClasses.Add(classFullName);

            if(!_classNameToDocsDic.TryGetValue(classFullName, out var lstFiles))
            {
                lstFiles = new List<string>();
                _classNameToDocsDic.Add(classFullName, lstFiles);
            }
            lstFiles.Add(filePath);
        }
    }

    /// <summary>
    /// 从 .csproj 文件中读取所有的 .cs 文件
    /// </summary>
    /// <returns></returns>
    List<string> GetSourceFilesFromCsProj()
    {
        List<string> ret = new List<string>();

        var csprojPath = $"{assemblyName}.csproj";
        var text = File.ReadAllText(csprojPath);

        var reg = new Regex("<Compile Include=\"(.+)\" />");
        int start = 0;
        while(true)
        {
            var match = reg.Match(text, start);
            if (match.Success)
            {
                ret.Add(match.Groups[1].Value.Replace('\\', '/'));
                start = match.Index + match.Length;
            }
            else
                break;
        }
        return ret;
    }
}
