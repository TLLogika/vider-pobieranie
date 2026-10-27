namespace ViderDownloader
{
    public sealed class QualityDecision
    {
        public bool Found;
        public bool Changed;
        public bool Pending;
        public bool Unsupported;
        public string Label;
        public int Width;
        public int Height;
        public int Count;
        public string Source;
    }

    public static class QualityPolicy
    {
        // Uses quality variants exposed by the player; never invents HD URLs.
        public const string Script = @"(()=>{
            const videos=[...document.querySelectorAll('video')];
            const v=videos.find(x=>x.currentSrc)||videos[0];
            const dims=()=>({Width:v?v.videoWidth:0,Height:v?v.videoHeight:0});
            const height=q=>Number(q.height||q.size||q.res||0)||(String(q.label||q.title||'').match(/(4320|2160|1440|1080|720|576|480|360|304|240)/)||[])[1]*1||(/4k/i.test(q.label||'')?2160:0);
            const score=q=>height(q)*1e12+Number(q.width||0)*1e6+Number(q.bitrate||0);
            const absolute=s=>{if(!s)return '';try{return new URL(s,location.href).href}catch(e){return ''}};
            const unsupported=s=>/\.(m3u8|mpd|webm)(?:[?#]|$)/i.test(s||'');
            try{
                if(typeof jwplayer==='function'){
                    const p=jwplayer(),levels=p.getQualityLevels?p.getQualityLevels():[];
                    if(levels&&levels.length){
                        const choices=levels.map((q,i)=>({q,i})).filter(x=>!/^auto$/i.test(x.q.label||''));
                        if(choices.length){
                            choices.sort((a,b)=>score(b.q)-score(a.q));const best=choices[0];
                            const item=p.getPlaylistItem?p.getPlaylistItem():{};
                            const sources=item.sources||[];
                            const source=sources.find(s=>s.label&&s.label===best.q.label)||(sources.length===levels.length?sources[best.i]:null);
                            const url=absolute(source&&source.file||item.file||(sources.length===1&&sources[0].file)||'');
                            const result={Found:true,Count:choices.length,Label:best.q.label||(height(best.q)?height(best.q)+'p':'Źródłowy MP4'),Source:url,...dims()};
                            if(unsupported(url))return {...result,Unsupported:true};
                            const key=String(best.i)+'|'+String(best.q.label||'');
                            if(p.getCurrentQuality&&p.setCurrentQuality&&p.getCurrentQuality()!==best.i){
                                if(window.__viderBestSwitch&&window.__viderBestSwitch.key===key)return {...result,Pending:true};
                                window.__viderBestSwitch={key,src:v&&v.currentSrc||'',at:Date.now()};p.setCurrentQuality(best.i);
                                return {...result,Changed:true,Pending:true};
                            }
                            const pending=window.__viderBestSwitch;
                            if(pending&&pending.key===key){
                                if(v&&v.currentSrc&&(v.currentSrc===url||v.currentSrc!==pending.src))delete window.__viderBestSwitch;
                                else return {...result,Pending:true};
                            }
                            return result;
                        }
                    }
                }
            }catch(e){}
            if(v){
                const sources=[...v.querySelectorAll('source')].map(s=>({node:s,file:s.src,label:s.label||s.getAttribute('label')||s.getAttribute('title')||'',height:s.getAttribute('data-res')||s.getAttribute('size')||s.getAttribute('res')||0}));
                const ranked=sources.filter(s=>height(s)>0).sort((a,b)=>score(b)-score(a));
                if(ranked.length){
                    const best=ranked[0],url=absolute(best.file),result={Found:true,Count:sources.length,Label:best.label||(height(best)+'p'),Source:url,...dims()};
                    if(unsupported(url))return {...result,Unsupported:true};
                    if(absolute(v.currentSrc||v.src||'')!==url){v.src=url;v.load();return {...result,Changed:true,Pending:true};}
                    return result;
                }
                const src=absolute(v.currentSrc||v.src||sources[0]&&sources[0].file||'');
                if(src)return {Found:true,Count:1,Label:'Źródłowy MP4',Source:src,Unsupported:unsupported(src),...dims()};
            }
            return {Found:false,Count:0,Label:'Nie ustalono wariantów'};
        })()";
    }
}
