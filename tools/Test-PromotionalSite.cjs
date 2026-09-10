'use strict';
// Static website checks only. Does not launch the application, browser or a provider.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../docs/site');
let count=0;
function walk(directory){for(const item of fs.readdirSync(directory,{withFileTypes:true})){
  const file=path.join(directory,item.name);assert(!item.isSymbolicLink(),'no linked publication files');
  assert(!['.git','.openai','node_modules','bin','obj','profiles'].includes(item.name),'no private/build state');
  if(item.isDirectory()){walk(file);continue;}
  assert(item.isFile());assert(!/\.(pfx|p12|pem|dll|exe|msix|mp3|m4a|flac|wav|mp4)$/i.test(file),'no binary packages, keys, or uncleared personal media');
  count++;
  if(file.endsWith('.js'))new vm.Script(fs.readFileSync(file,'utf8'),{filename:file});
  if(file.endsWith('.html')){
    const html=fs.readFileSync(file,'utf8'),ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);
    assert.equal(new Set(ids).size,ids.length,`unique IDs: ${file}`);
    for(const [,url] of html.matchAll(/(?:src|href)="([^"]+)"/g)){
      if(url.startsWith('#'))continue;
      if(/^https:\/\//.test(url)){assert(url.startsWith('https://github.com/ZCH-czc/winplayer')||url==='https://www.bilibili.com/video/BV1jY8t6JE9K','only public core and selected official work links');continue;}
      assert(!url.startsWith('/')&&!url.includes('://'),'project-subdirectory-compatible URL');
      const target=path.resolve(path.dirname(file),url.split(/[?#]/)[0]);
      assert(target.startsWith(root+path.sep));assert(fs.statSync(target).isFile(),`asset: ${url}`);
    }
  }
}}
walk(root);
const provenance=JSON.parse(fs.readFileSync(path.join(root,'player/provenance.json')));
for(const [file,hash] of Object.entries(provenance.files))if(file!=='index.html')assert.equal(crypto.createHash('sha256').update(fs.readFileSync(path.join(root,'player',file))).digest('hex'),hash,`public UI provenance: ${file}`);
for(const name of ['index.html','customize.html']){
 const html=fs.readFileSync(path.join(root,'player',name),'utf8');
 assert(html.includes("connect-src 'none'; media-src 'none'"),'presentation frame blocks connections and media');
}
console.log(`PASS ${count} public website files: syntax, asset closure, subdirectory paths, source provenance and presentation boundaries.`);
