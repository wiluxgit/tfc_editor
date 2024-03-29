using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lzo64;
using zlib;

namespace PaladinsTfc {
  class TexHandling
  {
    private static bool logPeekTexData = true;
    private static bool logTexDataMake = false;
    private static bool logTexData = false;
    private static bool checkCompression = false;
    private static bool writeTexture = false;
    private static bool compareReplacement = false;

    const int magicIdDXT1 = 0x31545844; //DXT1
    const int magicIdDXT5 = 0x35545844; //DXT5

    public static ReplacementCreator.Encoding encodingFromMagic(int i) {
      switch (i) {
        case magicIdDXT1:
          return ReplacementCreator.Encoding.DXT1;
        case magicIdDXT5:
          return ReplacementCreator.Encoding.DXT5;
        default:
          string s = Encoding.ASCII.GetString(
            BitConverter.GetBytes(i).Reverse().ToArray()
          );
          throw new ArgumentException($"can not understand format {s}");
      }
    }

    private string globalOutDir;
    private string inFile;
    private bool dumpEverything;
    private Dictionary<int, CLIOp> id2op;
    private CompressionMode.Modes defaultCompressionMode = CompressionMode.Modes.LZO;

    public class CompressionMode {
      public enum Modes {
        None, LZO, ZLib
      }
      public static Modes fromString(string str) {
        switch (str.ToUpper()) {
          case "NONE":
            return Modes.None;
          case "LZO":
            return Modes.LZO;
          case "ZLIB":
            return Modes.ZLib;
          default:
            throw new ArgumentException($"{str} is not a valid format");
        }
      }
    }
    public enum ReplacementTextureReader {
      Raw, Png, Dds
    }
    public struct CLIReplacement {
      public string replacementPath;
      public ReplacementTextureReader reader;
    }
    public class CLIOp {
      public bool dump;
      public CompressionMode.Modes? overrideCompressMode;
      public CLIReplacement? replacement;

      public CLIOp(bool dump) {
        this.dump = dump;
        this.overrideCompressMode = null;
        this.replacement = null;
      }
    }

    public class TexBlock {
      public long loc_compressedSize;
      public int compressedSize;
      public long loc_originalSize;
      public int originalSize;

      public long loc_texBlockStart; //actual compressed data begins here
    }
    public class Tex {
      public int id;

      public long loc;
      public long loc_unknown1;
      public int unknown1;
      public long loc_totalTextureBytes;
      public int totalTextureBytes;
      public long loc_unknown2;
      public int unknown2;
      public long loc_blockHeaderStart;

      public List<TexBlock> blocks;
    }
    public class TFCInfo {
      public string srcFile;
      public List<Tex> texs;
    }

    private bool isInDumpRange(int id){
      if (dumpEverything) {
        return true;
      } else {
        if(id2op.TryGetValue(id, out CLIOp op)) {
          return op.dump;
        }
        return false;
      }      
    }
    private CompressionMode.Modes getDecompressMode(int id, int compressedSize, int originalSize) {
      if (id2op.TryGetValue(id, out CLIOp op)) {
        if(op.overrideCompressMode.HasValue) {
          return op.overrideCompressMode.Value;
        }
      }
      return defaultCompressionMode;
    }
    private static bool tryRead(BinaryReader reader){
      try{
        if (reader.ReadUInt32() == 0x9e2a83c1) { //Found start of Texture, some magic header constant
          return false;
        } else {
          //Console.WriteLine("why am i reading in the wrong location?");
          return true;
        }
      } catch(System.IO.EndOfStreamException) {
        Console.WriteLine("Reached end of file");
        Environment.Exit(0);
        return false;
      }
    }
    private static string peek(byte[] bs, int nPeek){
      string sx = "";
      for (int i = 0; i < nPeek; i++){
        if(i > 0 && i %4 == 0) sx+=" |";
        sx+= (" "+bs[i].ToString("x2"));
      }
      return sx;
    }
    private static void printpeek(FileStream fs, int nPeek){
      if( logPeekTexData == false ) return;

      long loc = fs.Position;
      byte[] bs = new byte[nPeek];
      fs.Read(bs, 0, (int)nPeek);
      fs.Seek(-((long)nPeek), SeekOrigin.Current);

      string sx = "F[0x" + loc.ToString("X8") + "] =";
      Console.WriteLine(sx + peek(bs, nPeek));
    }
    
    private static TFCInfo getTFCInfo(string tfcFile){
      if (Path.GetExtension(tfcFile).ToLower() != ".tfc") {
        throw new ArgumentException("File is not a TFC file");
      }

      FileStream fs = new FileStream(tfcFile, FileMode.Open);
      BinaryReader br = new BinaryReader((Stream) fs);
      
      //Tfc Texture 
      /* LITLLE ENDIAN!!
      C1 83 2A 9E | Block start identifier
      00 00 02 00 | ? (same for all textures)
      D2 72 04 00 | remaningBytes (total bytes for whole texture)
      00 00 08 00 | some kind of dimension info?
      29 1C 01 00 | compressedTextureBlockSize[0]
      00 00 02 00 | originalTextureBlockSize[0]
      D3 36 01 00 | compressedTextureBlockSize[1] 
      00 00 02 00 | originalTextureBlockSize[1] 
      ...
      XX XX XX XX | compressed texture blocks after each other, no separator or header
      ...
      C1 83 2A 9E | Next Block start identifier
      */

      List<Tex> texs = new List<Tex>();
      while (fs.Position < fs.Length) {
        long loc_searchStart = fs.Position;
        while (tryRead(br)) 
          fs.Seek(-3L, SeekOrigin.Current);
        long loc_textureStart = fs.Position;

        if(logTexDataMake) {
          Console.WriteLine("FOUND tex " + texs.Count + " at 0x" + loc_textureStart.ToString("X8") + " (searched from 0x" + loc_searchStart.ToString("X8") + ")");
          printpeek(fs, 32);
          Console.WriteLine("Begin blocks");
        }
        
        Tex tex = new Tex();
        tex.loc = fs.Position;
        tex.loc_unknown1 = fs.Position;
        tex.unknown1 = br.ReadInt32();
        tex.loc_totalTextureBytes = fs.Position;
        tex.totalTextureBytes = br.ReadInt32(); 
        tex.loc_unknown2 = fs.Position;
        tex.unknown2 = br.ReadInt32(); 
        tex.loc_blockHeaderStart = fs.Position;
        tex.id = texs.Count;

        tex.blocks = new List<TexBlock>();
        int remaningBytes = tex.totalTextureBytes;
        while (remaningBytes > 0) {
          TexBlock block = new TexBlock();
          block.loc_compressedSize = fs.Position;
          block.compressedSize = br.ReadInt32();
          block.loc_originalSize = fs.Position;
          block.originalSize = br.ReadInt32();

          tex.blocks.Add(block);
          remaningBytes -= block.compressedSize;
        }

        if (logTexDataMake){
          Console.WriteLine(String.Format("tex {0} @ {1}, NBLOCKS = {2}, NBYTE = {3}, BLOCKHEADER = {4}, LOC:TEXNBYTE = {5}",
            tex.id,
            "0x" + tex.loc.ToString("X8"),
            tex.blocks.Count,
            "0x" + tex.totalTextureBytes.ToString("X8"),
            "0x" + tex.loc_blockHeaderStart.ToString("X8"),
            "0x" + tex.loc_totalTextureBytes.ToString("X8")
          ));
        }

        long offset = 0;
        for (int i = 0; i < tex.blocks.Count; i++){
          tex.blocks[i].loc_texBlockStart = tex.loc_blockHeaderStart + tex.blocks.Count*8 + offset;
          offset += tex.blocks[i].compressedSize;
        }
        TexBlock last = tex.blocks[tex.blocks.Count-1];
        long endoftexA = last.loc_texBlockStart + last.compressedSize;
        long endoftexB = tex.loc_blockHeaderStart + tex.blocks.Count*8 + tex.totalTextureBytes;

        //Console.WriteLine("loc_texBlockStart: 0x" + last.loc_texBlockStart.ToString("X8"));
        //Console.WriteLine("loc_blockHeaderStart: 0x" + tex.loc_blockHeaderStart.ToString("X8"));
        if(endoftexA != endoftexB){
          fs.Seek(tex.loc, SeekOrigin.Begin);
          printpeek(fs, 32);
          throw new Exception(String.Format("I am dumb 0x{0} != 0x{1}", endoftexA.ToString("X8"), endoftexB.ToString("X8")));
        }
        fs.Seek(endoftexA, SeekOrigin.Begin);

        /*if(tex.id == 126){
          throw new Exception("STOP");
        }/**/

        texs.Add(tex);

        if (logTexDataMake) 
          Console.WriteLine(string.Format("Tex {0} done, a' {1}", tex.id, tex.blocks.Count()));
      }

      fs.Close();
      br.Close();

      TFCInfo tfcinfo = new TFCInfo();
      tfcinfo.srcFile = tfcFile;
      tfcinfo.texs = texs;

      Console.WriteLine(string.Format("Indexed {0}, contains {1} textures", tfcinfo.srcFile, tfcinfo.texs.Count()));
      
      return tfcinfo;
    }
    
    //this garbage piece of shit code creates CLR memory misalignment something
    private void dumpTex(Tex tex, FileStream fs, LZOCompressor lzo, string fileName){
      if (tex.blocks.Count == 1 && tex.blocks[0].originalSize < 0x0800) {
        Console.WriteLine(String.Format("Texture {0} is too small, skipping", tex.id));
        return;
      }
      if (tex.blocks.Count > 128) {
        Console.WriteLine(String.Format("Texture {0} is too large, skipping", tex.id));
        return;
      }

      //Console.WriteLine(String.Format("Dumping Texture {0}", tex.id));

      bool couldInfer = inferImageProperties(tex, out int? ddsW, out int? ddsH, out int? ddsSize, out int? ddsFormat);
      if (couldInfer == false) {
        Console.WriteLine(String.Format("Texture {0} has non 2^x size, fail", tex.id));
        return;
      }
              
      string ddsFormatName = ddsFormat == magicIdDXT1 ? "DXT1" : "DXT5";
      string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
      string outPath = string.Format(globalOutDir + "/dump/{0}_{1}_{2}x{3}.dds", 
        withoutExtension, 
        tex.id.ToString("d3"),
        ddsW,
        ddsFormatName
      );
      Directory.CreateDirectory(Path.GetDirectoryName(outPath));
      FileStream fsDump = new FileStream(outPath, FileMode.Create);
      BinaryWriter bwDump = new BinaryWriter((Stream) fsDump);

      // DDS spec: https://docs.microsoft.com/en-us/windows/win32/direct3ddds/dx-graphics-dds-pguide
      // writing DDS header        
      bwDump.Write(0x20534444); // dwMagic (constant to identify that this is a dds file)
      bwDump.Write(0x7c);       // header
      bwDump.Write(0x1007);     // header DX10
      bwDump.Write(ddsH.Value);
      bwDump.Write(ddsW.Value);
      bwDump.Write(ddsSize.Value);
      bwDump.Write(0);
      bwDump.Write(1);
      fsDump.Seek(44L, SeekOrigin.Current);
      bwDump.Write(32);
      bwDump.Write(4);
      bwDump.Write(ddsFormat.Value);
      fsDump.Seek(40L, SeekOrigin.Current);

      var compressionMode = getDecompressMode(tex.id, tex.blocks[0].compressedSize, tex.blocks[0].originalSize);
      for (int i = 0; i < tex.blocks.Count; ++i) {
        TexBlock block = tex.blocks[i];
        //Console.WriteLine($"Block {i}: [uncompsize={block.originalSize.ToString("x")}, compsize={block.compressedSize.ToString("x")}]");

        fs.Seek(block.loc_texBlockStart, SeekOrigin.Begin);
          
        byte[] compressedTextureBlock = new byte[block.compressedSize];
        fs.Read(compressedTextureBlock, 0, block.compressedSize);
        switch (compressionMode) {
          case CompressionMode.Modes.LZO:
            byte[] buffer = lzo.Decompress(compressedTextureBlock, block.originalSize);
            fsDump.Write(buffer, 0, block.originalSize);
            break;
          case CompressionMode.Modes.ZLib:
            ZOutputStream zoutputStream = new ZOutputStream(fsDump);
            zoutputStream.Write(compressedTextureBlock, 0, block.originalSize);
            zoutputStream.Flush();
            break;
          case CompressionMode.Modes.None:
            fsDump.Write(compressedTextureBlock, 0, block.originalSize);
            break;
          default:
            throw new Exception("Decompression failure, not \"none\", \"lzo\" or \"zlib\"");
        }
      }

      int b0comp = tex.blocks[0].compressedSize;
      int b0org = tex.blocks[0].originalSize;
      Console.WriteLine($"Dumping Texture {tex.id} with {compressionMode}. comp:{b0comp}, uncomp:{b0org}");
      GC.Collect();
    }

    private void replaceTexture(TFCInfo tfcinfo, FileStream fs, LZOCompressor lzo, int id, CLIReplacement replacement) {
      Tex tex = tfcinfo.texs[id];
      if (tex.id != id)
        throw new Exception("RedundantDataException, tex id is wrong.");

      string path = replacement.replacementPath;

      Console.WriteLine("Replacing tex " + tex.id +  " with " + path);

      fs.Seek(tex.loc, SeekOrigin.Begin);
      Console.Write("Replace pre :");
      printpeek(fs, 32);

      long loc_texDataStart = tex.blocks[0].loc_texBlockStart;

      //overwrite whole source data with 0, this is maybe unneccesary but makes the output easier to diagnose
      foreach (TexBlock block in tex.blocks){ 
        fs.Seek(block.loc_texBlockStart, SeekOrigin.Begin);
        fs.Write(new byte[block.compressedSize], 0, block.compressedSize); 
      }

      bool couldInfer = inferImageProperties(tex, out int? ddsW, out int? ddsH, out int? ddsSize, out int? ddsFormat);
      if (couldInfer == false) {
        Console.WriteLine(String.Format("Texture {0} has non 2^x size, fail", tex.id));
        return;
      }
      ReplacementCreator.Encoding srcEncoding = encodingFromMagic(ddsFormat.Value);

      ReplacementCreator rc = new ReplacementCreator(path);
      Stream fsImg = rc.Serialize(ddsW.Value, srcEncoding);
      int imgLen = checked((int)fsImg.Length) - 0x80;

      //replacement validty
      {
        long totalOriginalBytes = 0;
        foreach (var block in tex.blocks) {
          totalOriginalBytes += block.originalSize;
        }
        if (totalOriginalBytes != imgLen) {
          long srcLen = File.OpenRead(path).Length;
          throw new Exception("Texture is not same size even after scaling, Mismatching format or proportions?" +
            $"\n org:{totalOriginalBytes.ToString("x8")}" +
            $"\n now:{imgLen.ToString("x8")}" +
            $"\n srcFile:{srcLen.ToString("x8")}"
           );
        }
      }

      //printpeek(fsImg,32);
      fsImg.Seek(0x80, SeekOrigin.Begin);  //strip dds header
      //printpeek(fsImg,32);
      long texWriteLoc = loc_texDataStart;
      int nWrittenBytes = 0;

      foreach (TexBlock block in tex.blocks) {
        byte[] readBlock = new byte[block.originalSize];
        fsImg.Read(readBlock, 0, block.originalSize);

        /*
        byte[] readBlock2 = File.ReadAllBytes(replacementDDSPath).Skip(0x80).ToArray();
        if(!Enumerable.SequenceEqual(readBlock, readBlock2)){
          throw new Exception("AAAAAAAAAAAAAAAAA");
        } else {}*/

        //Console.WriteLine(readBlock[readBlock.Length-1] + " pos:" + fsImg.Position.ToString("X8") + " siz:" + readBlock.Length.ToString("X8"));

        byte[] lzo999compressed = lzo.Compress(readBlock);
        int newSize = lzo999compressed.Length;

        if (checkCompression){  
          byte[] recompress = lzo.Decompress(lzo999compressed, readBlock.Length);
          if (Enumerable.SequenceEqual(readBlock, recompress)) {
            Console.WriteLine("Compression works");
          } else{
            throw new Exception("Internal Compression Failure");
          }
        }
        
        fs.Seek(block.loc_compressedSize, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(newSize), 0, 4);

        // originalsize should (probably) not be tampered with. This would only be usefull if there is way to override resolution
        //fs.Seek(block.loc_originalSize, SeekOrigin.Begin); 
        //fs.Write(BitConverter.GetBytes(block.originalSize), 0, 4); 

        fs.Seek(texWriteLoc, SeekOrigin.Begin);
        fs.Write(lzo999compressed, 0, newSize);
        texWriteLoc += newSize;
        nWrittenBytes += newSize;

        /*Console.WriteLine(string.Format("uncompressed = 0x{0}: bytes: {1} -> {2}",
          block.originalSize.ToString("X8"),
          block.compressedSize.ToString("X8"),
          newSize.ToString("X8")
        ));*/
      }
      fsImg.Close();

      if (nWrittenBytes > tex.totalTextureBytes) {
        throw new Exception($"Image {replacement.replacementPath} can not be compressed to fit in {inFile}:{tex.id}. Try reducing noise or posterising the image");
      }
      fs.Seek(tex.loc_totalTextureBytes, SeekOrigin.Begin); 
      fs.Write(BitConverter.GetBytes(nWrittenBytes), 0, 4);

      fs.Seek(tex.loc, SeekOrigin.Begin);
      Console.Write("Replace post:");
      printpeek(fs, 32);

      Console.WriteLine(string.Format(
        "Replaced tex {0} a' {1}, totalTextureBytes: 0x{2} -> 0x{3}\n", 
        tex.id,
        tex.blocks.Count(),
        tex.totalTextureBytes.ToString("X8"), 
        nWrittenBytes.ToString("X8")
      ));
    }
    public bool inferImageProperties(Tex tex, 
      [NotNullWhen(true)] out int? ddsW,
      [NotNullWhen(true)] out int? ddsH,
      [NotNullWhen(true)] out int? ddsSize,
      [NotNullWhen(true)] out int? ddsFormat
    ) {
      switch (tex.blocks.Count) {
        case 2:
          ddsW = 512;
          ddsH = 512;
          ddsSize = ddsW * ddsH;
          ddsFormat = magicIdDXT5;
          break;
        case 4:
          ddsW = 1024;
          ddsH = 1024;
          ddsSize = ddsW * ddsH / 2;
          ddsFormat = magicIdDXT1;
          break;
        case 8:
          ddsW = 1024;
          ddsH = 1024;
          ddsSize = ddsW * ddsH;
          ddsFormat = magicIdDXT5;
          break;
        case 16:
          ddsW = 2048;
          ddsH = 2048;
          ddsSize = ddsW * ddsH / 2;
          ddsFormat = magicIdDXT1;
          break;
        case 32:
          ddsW = 2048;
          ddsH = 2048;
          ddsSize = ddsW * ddsH;
          ddsFormat = magicIdDXT5;
          break;
        case 64:
          ddsW = 4096;
          ddsH = 4096;
          ddsSize = ddsW * ddsH / 2;
          ddsFormat = magicIdDXT1;
          break;
        case 128:
          ddsW = 4096;
          ddsH = 4096;
          ddsSize = ddsW * ddsH;
          ddsFormat = magicIdDXT5;
          break;
        default:
          switch (tex.blocks[0].originalSize) {
            case 0x0800:
              ddsW = 64;
              ddsH = 64;
              ddsSize = ddsW * ddsH / 2;
              ddsFormat = magicIdDXT1;
              break;
            case 0x1000:
              ddsW = 64;
              ddsH = 64;
              ddsSize = ddsW * ddsH;
              ddsFormat = magicIdDXT5;
              break;
            case 0x2000:
              ddsW = 128;
              ddsH = 128;
              ddsSize = ddsW * ddsH / 2;
              ddsFormat = magicIdDXT1;
              break;
            case 0x4000:
              ddsW = 128;
              ddsH = 128;
              ddsSize = ddsW * ddsH;
              ddsFormat = magicIdDXT5;
              break;
            case 0x8000:
              ddsW = 256;
              ddsH = 256;
              ddsSize = ddsW * ddsH / 2;
              ddsFormat = magicIdDXT1;
              break;
            case 0x10000:
              ddsW = 256;
              ddsH = 256;
              ddsSize = ddsW * ddsH;
              ddsFormat = magicIdDXT5;
              break;
            case 0x20000:
              ddsW = 512;
              ddsH = 512;
              ddsSize = ddsW * ddsH / 2;
              ddsFormat = magicIdDXT1;
              break;
            default:
              ddsW = null;
              ddsH = null;
              ddsFormat = null;
              ddsSize = null;
              return false;
          }
          break;
      }
      return true;
    }

    public void run(
      string inFile,
      string globalOutDir,
      bool dumpEverything,
      Dictionary<int, CLIOp> id2op,
      CompressionMode.Modes defaultCompressionMode = CompressionMode.Modes.LZO
    ){
      this.inFile = inFile;
      this.globalOutDir = globalOutDir;
      this.dumpEverything = dumpEverything;
      this.id2op = id2op;
      this.defaultCompressionMode = defaultCompressionMode;

      TFCInfo tf = getTFCInfo(inFile);
      FileStream fsIn = new FileStream(inFile, FileMode.Open);

      if (true){
        LZOCompressor lzo = new LZOCompressor();
        foreach (Tex tex in tf.texs) {
          if (isInDumpRange(tex.id)) {
            dumpTex(tex, fsIn, lzo, inFile);
          }
        }
        fsIn.Close();
      }
      
      if(id2op != null && id2op.Count() > 0){
        LZOCompressor lzo = new LZOCompressor(); // NEVER EVER EVER SHARE LZOCompressor from dump
        string outFile = globalOutDir + "/edited/" + Path.GetFileName(inFile);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile));
        File.Copy(inFile, outFile, true);
        FileStream fsOut = new FileStream(outFile, FileMode.Open);
        foreach(KeyValuePair<int, CLIOp> kv in id2op) {
          CLIReplacement? rep = kv.Value.replacement;
          if(rep.HasValue) {
            replaceTexture(tf, fsOut, lzo, kv.Key, rep.Value);
          }
        }
        fsOut.Close();
      }

      Console.WriteLine("DONE");
    }
  }
}