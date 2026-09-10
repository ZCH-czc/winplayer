using System.Text;

// Minimal uncompressed AVI fixture: changing color bars, no user assets and no external encoder.
internal static class SyntheticVideo
{
    internal static void Write(string path)
    {
        const int width=160,height=90,frames=200,size=width*height*3;
        using var w=new BinaryWriter(File.Create(path),Encoding.ASCII);
        Chunk(w,"RIFF",w=>
        {
            Code(w,"AVI ");
            Chunk(w,"LIST",w=>
            {
                Code(w,"hdrl");
                Chunk(w,"avih",w=>{foreach(var n in new uint[]{40000,size*25,0,16,frames,0,1,size,width,height,0,0,0,0}) w.Write(n);});
                Chunk(w,"LIST",w=>
                {
                    Code(w,"strl");
                    Chunk(w,"strh",w=>
                    {
                        Code(w,"vids"); Code(w,"DIB "); w.Write(0u); w.Write((ushort)0);w.Write((ushort)0);
                        foreach(var n in new uint[]{0,1,25,0,frames,size,uint.MaxValue,0}) w.Write(n);
                        foreach(var n in new short[]{0,0,width,height}) w.Write(n);
                    });
                    Chunk(w,"strf",w=>
                    {
                        w.Write(40);w.Write(width);w.Write(height);w.Write((short)1);w.Write((short)24);
                        foreach(var n in new int[]{0,size,0,0,0,0}) w.Write(n);
                    });
                });
            });
            Chunk(w,"LIST",w=>
            {
                Code(w,"movi");
                for(var frame=0;frame<frames;frame++)
                    Chunk(w,"00db",w=>{for(var y=0;y<height;y++) for(var x=0;x<width;x++) { w.Write((byte)(frame%255)); w.Write((byte)(x*255/width)); w.Write((byte)(y*255/height)); }});
            });
            Chunk(w,"idx1",w=>{for(var i=0;i<frames;i++){Code(w,"00db");w.Write(16);w.Write(4+i*(size+8));w.Write(size);}});
        });
    }
    private static void Code(BinaryWriter writer,string code)=>writer.Write(Encoding.ASCII.GetBytes(code));
    private static void Chunk(BinaryWriter writer,string id,Action<BinaryWriter> body)
    {
        Code(writer,id);var offset=writer.BaseStream.Position;writer.Write(0);body(writer);
        var end=writer.BaseStream.Position;writer.BaseStream.Position=offset;writer.Write(checked((int)(end-offset-4)));writer.BaseStream.Position=end;
        if((end&1)!=0) writer.Write((byte)0);
    }
}
