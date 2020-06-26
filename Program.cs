using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;

namespace HtmlStaticInclude
{
    class Program
    {
        static string c_syntax =
@"Syntax HtmlStaticInclude <html filename> ...

   Multiple filenames can be specified and they can have wildcards.

   Syntax is similar to server-side includes (SSI). The difference is that
   HtmlStaticInclude performs the inclusion when the tool is executed thereby
   placing no runtime demand on the server. To do this, a tag is required at
   the beginning AND at the end of each included segment. The content between
   the tags will be replaced by the included file.

   Include tags are embedded in HTML comments so that they are ignored by the
   browser. Nevertheless they will be visible to anyone who examines the page
   source. Therefore, they should not give away any sensitive information.

   An include begins with the include tag as follows:
   <!--#sxi-include src=""/_includes/header.sxi"" -->

   or

   <!--#sxi-include file=""..\..\_includes/header.sxi"" -->

   An include ends with the endinclude tag as follows:
   <!--#sxi-endinclude-->

   Include tags may be nested. That is, a file that is included may, in turn
   have its own include tags. When tags are nested, the inner tags are
   resolved after the outer tags.

   The file to be included can be specified with a local filename using a 
   ""file"" attribute or with a web path usingn a ""src"" attribute. Either
   method can include relative paths using "".."" in the path.

   If the web path starts with a slash ""/"" then the root of the website
   must be determined. This is done by walking up the file system path until
   the web path is satisfied.

   You can also indicate where the root is by specifying the path to the
   current file with a ""this"" tag as follows:
   <!--#sxi-this src=""/index.htm"" -->

   The above tag indicates that the local file should be called ""index.htm""
   and that it's located in the root of the website.
";

        static void Main(string[] args)
        {
            try
            {
                if (args.Length == 0
                    || args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)
                    || args[0].Equals("-?", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(c_syntax);
                }
                else
                {
                    bool recursive = false;
                    foreach (var arg in args)
                    {
                        if (arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
                        {
                            recursive = true;
                        }
                        else
                        {
                            foreach (var filename in CodeBit.PathEx.GetFilesByPattern(arg, recursive))
                            {
                                ProcessFile(filename);
                            }
                        }
                    }
                }
            }
            catch (Exception err)
            {
#if DEBUG
                Console.WriteLine(err.ToString());
#else
                Console.WriteLine(err.Message);
#endif
            }

#if DEBUG
            if (System.Diagnostics.Debugger.IsAttached)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
            }
#endif
        }

        const string c_tempExtension = ".tmp";
        static Encoding s_Utf8NoBom = new UTF8Encoding(false);

        static void ProcessFile(string filename)
        {
            Console.WriteLine($"Processing: {filename}");
            string tempFilename = filename + c_tempExtension;

            try
            {
                bool result;
                using (var writer = new StreamWriter(tempFilename, false, s_Utf8NoBom))
                {
                    result = ProcessFile(filename, writer);
                }

                if (result)
                {
                    File.Delete(filename);
                    File.Move(tempFilename, filename);
                    tempFilename = null;
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFilename) && File.Exists(tempFilename))
                {
                    File.Delete(tempFilename);
                }
            }
        }

        const string c_tagPattern = "<!--#sxi--->";
        const int c_TagMiddle = 9;
        const int c_tagComplete = 12;

        // Returns true if anythign processed. Else, false
        static bool ProcessFile(string filename, TextWriter writer)
        {
            bool result = false;
            int depthFromWebRoot = -1; // -1 means unknown

            using (var reader = new StreamReader(filename, Encoding.UTF8, true))
            {
                for (; ; )
                {
                    var tag = ProcessUntilTag(reader, writer);
                    if (tag == null) break;

                    switch (tag.Label)
                    {
                        case "this":
                            {
                                string src;
                                if (!tag.Attributes.TryGetValue("src", out src))
                                {
                                    throw new ApplicationException($"sxi-this: Expected 'src' attribute. ({filename})");
                                }
                                src = src.ToLowerInvariant().Replace('\\', '/');
                                if (src[0] != '/'
                                    || !filename.ToLowerInvariant().Replace('\\', '/').EndsWith(src))
                                {
                                    throw new ApplicationException($"sxi-this: 'src' attribute must begin with slash and match tail of physical file path. ({filename})");
                                }
                                depthFromWebRoot = -1;
                                foreach (char c in src)
                                {
                                    if (c == '/') ++depthFromWebRoot;
                                }
                            }
                            break;

                        case "include":
                            result = true;
                            ProcessInclude(tag, filename, depthFromWebRoot, writer);
                            SkipPriorInclude(reader);
                            break;
                    }
                }
            }

            return result;
        }

        static void ProcessInclude(SxiTag tag, string outerFilename, int depthFromWebRoot, TextWriter writer)
        {
            // Locate the file to be included
            string filename = string.Empty;

            // Web path provided
            string src;
            if (tag.Attributes.TryGetValue("src", out src) && src.Length > 0)
            {
                // Convert to backslash notation
                src = src.Replace('/', '\\');

                // If path starts with a slash, we have to find the web root folder
                if (src[0] == '\\')
                {
                    // If depth from web root is provided, use that.
                    if (depthFromWebRoot >= 0)
                    {
                        var directory = outerFilename;
                        int firstSlash = directory.IndexOf('\\');

                        for (int i=-1; i<depthFromWebRoot; ++i)
                        {
                            int lastSlash = directory.LastIndexOf('\\');
                            if (lastSlash > firstSlash)
                                directory = directory.Substring(0, lastSlash);
                        }
                        filename = Path.Combine(directory, src.Substring(1));

                        if (!File.Exists(filename))
                        {
                            throw new ApplicationException($"sxi-include: Failed to find source using 'this' path. src=\"{tag.Attributes["src"]}\"");
                        }
                    }

                    // Else, walk up the tree until path combining works
                    else
                    {
                        var directory = outerFilename;
                        int firstSlash = directory.IndexOf('\\');
                        for (; ; )
                        {
                            int lastSlash = directory.LastIndexOf('\\');
                            if (lastSlash > firstSlash)
                                directory = directory.Substring(0, lastSlash);

                            filename = Path.Combine(directory, src.Substring(1));
                            if (File.Exists(filename)) break;

                            if (lastSlash <= firstSlash)
                            {
                                throw new ApplicationException($"sxi-include: Failed to find source. src=\"{tag.Attributes["src"]}\"");
                            }
                        }
                    }
                }

                else
                {
                    string file;
                    if (!tag.Attributes.TryGetValue("file", out file))
                    {
                        throw new ApplicationException("sxi-include: Must specify either 'src' or 'file' attribute.");
                    }
                    filename = Path.Combine(Path.GetDirectoryName(outerFilename), file);
                    if (!File.Exists(file))
                    {
                        throw new ApplicationException($"sxi-include: Failed to find source. file=\"{file}\"");
                    }
                }
            }

            // Write the contents of the include
            writer.WriteLine();
            ProcessFile(filename, writer);

            // Write the closing tag
            writer.Write("<!--#sxi-endinclude-->");
        }

        static void SkipPriorInclude(TextReader reader)
        {
            int depth = 1;
            while (depth > 0)
            {
                var tag = ProcessUntilTag(reader, null);
                if (tag == null)
                {
                    throw new ApplicationException("sxi-endinclude tag not found!");
                }

                switch (tag.Label)
                {
                    case "include":
                        ++depth;
                        break;

                    case "endinclude":
                        --depth;
                        break;
                }
            }
        }

        // Pass null for writer to skip, pass a value for writer to copy
        static SxiTag ProcessUntilTag(TextReader reader, TextWriter writer)
        {
            int matchIndex = 0;
            var tag = new StringBuilder();

            for (; ; )
            {
                int ci = reader.Read();
                if (ci < 0) break;
                char c = (char)ci;

                if (writer != null)
                {
                    writer.Write(c);
                }

                // See if matching tag pattern
                if (c == c_tagPattern[matchIndex])
                {
                    tag.Append(c);

                    ++matchIndex;
                    if (matchIndex >= c_tagComplete)
                    {
                        return ParseTag(tag.ToString());
                    }
                }
                else
                {
                    if (matchIndex >= c_TagMiddle)
                    {
                        tag.Append(c);
                        matchIndex = c_TagMiddle;
                    }
                    else if (matchIndex > 0)
                    {
                        tag.Clear();
                        matchIndex = 0;
                    }
                }
            }

            return null;
        }

        // This method assumes the tag is well-formed and doesn't do much in the way of error checking.
        // A comment that's not a tag (but still starts with #sxi-) will still return a result but
        // the label may be an empty string or an unexpected value. Likewise, the attributes may
        // not match what's expected. It's up to the caller to check for errors in the resulting
        // values.
        static SxiTag ParseTag(string tag)
        {
            Debug.Assert(tag.StartsWith("<!--#sxi-"));
            Debug.Assert(tag.EndsWith("-->"));

            int i = 9;
            int end = tag.Length - 3;

            // Parse label
            int a = i; // anchor
            while (i < end && !char.IsWhiteSpace(tag[i])) ++i;
            string label = tag.Substring(a, i - a);

            // Parse attributes
            var attrs = new Dictionary<string, string>();
            for (; ; )
            {
                // Skip whitespace
                while (i < end && char.IsWhiteSpace(tag[i])) ++i;

                // Attribute Key
                a = i;
                while (i < end && tag[i] != '=' && !char.IsWhiteSpace(tag[i])) ++i;
                var key = tag.Substring(a, i - a);

                // Clear Equals
                while (i < end && tag[i] != '=') ++i;
                if (i < end) ++i;

                // Attribute Value
                while (i < end && tag[i] != '"') ++i;
                if (i < end) ++i;
                a = i;
                while (i < end && tag[i] != '"') ++i;
                var value = tag.Substring(a, i - a);
                if (i < end) ++i;

                if (string.IsNullOrEmpty(key)) break;

                attrs.Add(key, value);
            }

            return new SxiTag(label, attrs);
        }

        private class SxiTag
        {
            public SxiTag(string label, IReadOnlyDictionary<string, string> attributes)
            {
                Label = label;
                Attributes = attributes;
            }

            public string Label { get; private set; }
            public IReadOnlyDictionary<string, string> Attributes { get; private set; }
        }
    }

}
