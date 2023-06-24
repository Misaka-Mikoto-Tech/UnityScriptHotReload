using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AssemblyPatcher;

/// <summary>
/// 扫描可能的 partial class 文件
/// </summary>
public class PartialClassScanner
{
    public string assemblyName { get;private set; }
    public List<string> changedFiles { get; private set; }
    public string[] defines { get; private set; }
    /// <summary>
    /// 所有需要编译的文件列表
    /// </summary>
    public HashSet<string> allFilesNeeded { get; private set; } = new HashSet<string>();


    CSharpParseOptions _parseOpt;
    /// <summary>
    /// (file, partial class) 映射
    /// </summary>
    Dictionary<string, List<string>> _filePartialClassesDic = new Dictionary<string, List<string>>();
    /// <summary>
    /// (partial class, file list) 映射
    /// </summary>
    Dictionary<string, List<string>> _classNameToFilesDic = new Dictionary<string, List<string>>();
    object _locker = new object();

    public PartialClassScanner(string assemblyName, List<string> changedFiles, string[] defines)
    {
        this.assemblyName = assemblyName;
        this.changedFiles = changedFiles;
        this.defines = defines;

        _parseOpt = new CSharpParseOptions(LanguageVersion.Default, DocumentationMode.None, SourceCodeKind.Regular, defines);

        foreach (var file in changedFiles)
        {
            allFilesNeeded.Add(file);
        }
    }

    public void Scan()
    {
        // 首先扫描发生改变的文件，如果其中没有 partial class，则停止扫描
        foreach(var file in changedFiles)
            ScanFile(file);

        if (_filePartialClassesDic.Count == 0)
            return;

        var csFiles = GetSourceFilesFromCsProj();
        // 开多个线程开始扫描
        foreach(var csFile in csFiles)
        {
            if (changedFiles.Contains(csFile))
                continue;
            ScanFile(csFile);
        }


        // filter partial class files
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
                    string fullNameCls = getFullName();
                    UpdatePartialClassData(filePath, fullNameCls);
                    break;
            }
        }
    }

    void UpdatePartialClassData(string filePath, string classFullName)
    {
        lock (_locker)
        {
            if(!_filePartialClassesDic.TryGetValue(filePath, out var lstClasses))
            {
                lstClasses = new List<string>();
                _filePartialClassesDic.Add(filePath, lstClasses);
            }
            lstClasses.Add(classFullName);

            if(!_classNameToFilesDic.TryGetValue(classFullName, out var lstFiles))
            {
                lstFiles = new List<string>();
                _classNameToFilesDic.Add(classFullName, lstFiles);
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
