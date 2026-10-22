"""Original Purr2D2 hilt, portable GLB/PNG authoring and CPU preview renderer.

Run python3 tools/create_hilt_portable.py (Pillow required). This is the shipping
asset source when Blender cannot start. create_hilt.py is the optional Blender
authoring implementation; no .blend is claimed unless Blender saves it.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import json, math, struct, random

ROOT=Path(__file__).resolve().parents[1]
MODELS=ROOT/'Assets/Models'; TEX=ROOT/'Assets/Textures'; ART=ROOT/'art'
for folder in (MODELS,TEX,ART): folder.mkdir(parents=True,exist_ok=True)
TAU=math.tau; SIZE=1024
# RGB swatches are sRGB; data maps below remain linear channel values.
PALETTE=[((146,165,180),.30,.93),((25,34,42),.64,.08),
         ((65,79,91),.38,.72),((193,143,80),.30,.88),
         ((35,199,222),.26,.42),((10,15,21),.68,.20),
         ((206,218,222),.45,.12),((193,211,221),.20,.98)]
random.seed(2210)
diff=Image.new('RGBA',(SIZE,SIZE)); orm=Image.new('RGBA',(SIZE,SIZE)); norm=Image.new('RGBA',(SIZE,SIZE))
dp,op,np=diff.load(),orm.load(),norm.load()
for y in range(SIZE):
    band=y//128; color,rough,metal=PALETTE[band]
    for x in range(SIZE):
        brushed=(.018*math.sin(x*.83)+.012*math.sin(x*2.11+y*.017)+random.gauss(0,.004))
        amount=1+brushed*(1 if band in (0,2,3,7) else .3)
        dp[x,y]=tuple(max(0,min(255,round(c*amount))) for c in color)+(255,)
        op[x,y]=(255,round(255*rough),round(255*metal),255)
        np[x,y]=(round(127.5+3*math.sin(x*.83)),round(127.5+.75*math.sin(y*.11)),255,255)
diff.save(TEX/'Purr2D2_Hilt_Diffuse.png');orm.save(TEX/'Purr2D2_Hilt_ORM.png');norm.save(TEX/'Purr2D2_Hilt_Normal.png')

positions=[]; normals=[]; uvs=[]; indices=[]; vertex_bands=[]
def dot(a,b):return sum(x*y for x,y in zip(a,b))
def sub(a,b):return tuple(x-y for x,y in zip(a,b))
def cross(a,b):return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def unit(a):
    length=math.sqrt(dot(a,a))
    return tuple(x/(length or 1) for x in a)
def vertex(p,n,u,v,band):
    index=len(positions);positions.append(p);normals.append(n)
    # glTF texture origin top-left: band0 is at top of PNG.
    uvs.append((.02+.96*u,(band+.06+.88*v)/8));vertex_bands.append(band)
    return index
def lathe(profile,band,segments=48,center=(0,0),axis='z',caps=True):
    zlo=min(x[0] for x in profile);zhi=max(x[0] for x in profile)
    start=len(positions)
    def orient(p):
        x,y,z=p
        return (x+center[0],y+center[1],z) if axis=='z' else (x,-z,y)
    def normal_orient(p):return p if axis=='z' else (p[0],-p[2],p[1])
    for row,(z,r) in enumerate(profile):
        prev=profile[max(0,row-1)];next_=profile[min(len(profile)-1,row+1)]
        dz=next_[0]-prev[0];dr=next_[1]-prev[1]
        for i in range(segments+1):
            t=i*TAU/segments;c,s=math.cos(t),math.sin(t)
            vertex(orient((r*c,r*s,z)),normal_orient(unit((dz*c,dz*s,-dr))),i/segments,(z-zlo)/(zhi-zlo or 1),band)
    for row in range(len(profile)-1):
        for i in range(segments):
            a=start+row*(segments+1)+i;b=a+1;c=a+segments+1;d=c+1
            indices.extend((a,b,d,a,d,c))
    for end,sign in (((0,-1),(len(profile)-1,1)) if caps else ()):
        z,r=profile[end];startcap=len(positions)
        vertex(orient((0,0,z)),normal_orient((0,0,sign)),.5,.5,band)
        for i in range(segments+1):
            t=i*TAU/segments;c,s=math.cos(t),math.sin(t)
            vertex(orient((r*c,r*s,z)),normal_orient((0,0,sign)),.5+.47*c,.5+.47*s,band)
        for i in range(segments):
            a=startcap;b=a+i+1;c=b+1
            indices.extend((a,c,b) if sign<0 else (a,b,c))

def cylinder(radius,z0,z1,band,segments=32,center=(0,0),bevel=.0003):
    bevel=min(bevel,(z1-z0)/3,radius/3)
    profile=([(z0,radius),(z1,radius)] if bevel<=0 else
             [(z0,radius-bevel),(z0+bevel,radius),(z1-bevel,radius),(z1,radius-bevel)])
    lathe(profile,band,segments,center)

lathe([(-.22,.012),(-.219,.0165),(-.216,.019),(-.202,.019),(-.199,.016)],0)
for z in (-.215,-.210,-.205):cylinder(.0192,z,z+.0018,2,32,bevel=.0002)
cylinder(.0195,-.200,-.1975,3)
cylinder(.0152,-.198,-.085,1,48)
for i in range(15):
    z=-.194+i*.007
    lathe([(z,.0152),(z+.0008,.0165),(z+.002,.0165),(z+.003,.0152)],1,24)
for angle in (math.pi/4,3*math.pi/4,5*math.pi/4,7*math.pi/4):
    cylinder(.0014,-.187,-.094,0,12,(.016*math.cos(angle),.016*math.sin(angle)),.0002)
lathe([(-.092,.016),(-.089,.018),(-.052,.018),(-.048,.015)],0)
cylinder(.0182,-.09,-.0878,3,32)
cylinder(.0182,-.055,-.052,2,32)
lathe([(-.051,.013),(-.047,.013),(-.045,.0145),(-.031,.0145),(-.028,.017)],2,32)
cylinder(.0149,-.040,-.0365,4)
cylinder(.0153,-.041,-.040,7,32)
cylinder(.0153,-.0365,-.0355,7,32)
lathe([(-.030,.017),(-.027,.021),(-.008,.021),(-.004,.0195),(-.002,.017)],0)
cylinder(.0212,-.020,-.0175,2)
cylinder(.0213,-.010,-.0085,3)
# Aperture is a shallow annulus with a dark inner well; no bottom-reaching blade.
lathe([(-.002,.017),(-.0006,.017),(0,.013),(-.0001,.010),(-.006,.010)],5,caps=False)
cylinder(.0095,-.0015,-.0003,3)
cylinder(.0075,-.0002,0,4,48,bevel=0)

def button(radius,depth,y,z,band):
    # A cylinder along -Y using the lathe's axis mapping.
    start=len(positions)
    lathe([(-depth/2,radius-.0003),(-depth/2+.0003,radius),(depth/2-.0003,radius),(depth/2,radius-.0003)],band,32,axis='y')
    for i in range(start,len(positions)):
        p=positions[i];positions[i]=(p[0],p[1]+y,p[2]+z)
button(.0063,.003,-.018,-.071,2)
button(.0054,.003,-.020,-.071,3)
button(.0038,.0015,-.022,-.071,4)

triangles=len(indices)//3
print('HILT_TRIANGLES',triangles,flush=True)
assert triangles<=10000,triangles

# Tangent basis follows the atlas UVs; ship it explicitly rather than relying
# on loader generation. Separate cap vertices preserve their hard normals.
tangent_sum=[[0.,0.,0.] for _ in positions];bitangent_sum=[[0.,0.,0.] for _ in positions]
for face in range(0,len(indices),3):
    a,b,c=indices[face:face+3];e1=sub(positions[b],positions[a]);e2=sub(positions[c],positions[a])
    t1=sub(uvs[b],uvs[a]);t2=sub(uvs[c],uvs[a]);det=t1[0]*t2[1]-t1[1]*t2[0]
    if abs(det)<1e-12:continue
    tan=tuple((t2[1]*e1[j]-t1[1]*e2[j])/det for j in range(3))
    bitan=tuple((-t2[0]*e1[j]+t1[0]*e2[j])/det for j in range(3))
    for index in (a,b,c):
        for j in range(3):tangent_sum[index][j]+=tan[j];bitangent_sum[index][j]+=bitan[j]
tangents=[]
for n,t,b in zip(normals,tangent_sum,bitangent_sum):
    tangent=unit(tuple(t[j]-n[j]*dot(n,t) for j in range(3)))
    if dot(tangent,tangent)<.5:tangent=unit(cross((0,1,0) if abs(n[1])<.9 else (1,0,0),n))
    tangents.append(tangent+(-1. if dot(cross(n,tangent),b)<0 else 1.,))

# GLB binary and JSON, one baked mesh/node/primitive, external PBR textures.
binary=bytearray();views=[];accessors=[]
def accessor(values,components,component_type,kind,target):
    while len(binary)%4:binary.append(0)
    offset=len(binary);fmt={5126:'f',5125:'I'}[component_type]
    flat=values if components==1 else [v for row in values for v in row]
    binary.extend(struct.pack('<'+fmt*len(flat),*flat))
    views.append({'buffer':0,'byteOffset':offset,'byteLength':len(binary)-offset,'target':target})
    item={'bufferView':len(views)-1,'componentType':component_type,'count':len(values),'type':kind}
    if kind=='VEC3' and len(accessors)==0:
        item['min']=[min(p[i] for p in values) for i in range(3)]
        item['max']=[max(p[i] for p in values) for i in range(3)]
    accessors.append(item);return len(accessors)-1
pa=accessor(positions,3,5126,'VEC3',34962);na=accessor(normals,3,5126,'VEC3',34962)
ua=accessor(uvs,2,5126,'VEC2',34962);ia=accessor(indices,1,5125,'SCALAR',34963)
ta=accessor(tangents,4,5126,'VEC4',34962)
document={'asset':{'version':'2.0','generator':'Purr2D2 original portable hilt generator'},
 'scene':0,'scenes':[{'nodes':[0]}],'nodes':[{'name':'Purr2D2_Hilt','mesh':0}],
 'meshes':[{'name':'Purr2D2_Hilt','primitives':[{'attributes':{'POSITION':pa,'NORMAL':na,'TEXCOORD_0':ua,'TANGENT':ta},'indices':ia,'material':0}]}],
 'materials':[{'name':'Purr2D2_Hilt_Material','pbrMetallicRoughness':{'baseColorTexture':{'index':0},'metallicRoughnessTexture':{'index':2},'metallicFactor':1,'roughnessFactor':1},'normalTexture':{'index':1,'scale':.28},'occlusionTexture':{'index':2}}],
 'textures':[{'source':i,'sampler':0} for i in range(3)],
 'images':[{'uri':'../Textures/Purr2D2_Hilt_'+suffix+'.png'} for suffix in ('Diffuse','Normal','ORM')],
 'samplers':[{'magFilter':9729,'minFilter':9987,'wrapS':33071,'wrapT':33071}],
 'buffers':[{'byteLength':len(binary)}],'bufferViews':views,'accessors':accessors,
 'extras':{'units':'meters','bladeAxis':'+Z','emitterOrigin':[0,0,0],'gripCenter':[0,0,-.12],'original':True}}
encoded=json.dumps(document,separators=(',',':')).encode();encoded+=b' '*((-len(encoded))%4)
binary+=b'\0'*((-len(binary))%4)
glb=struct.pack('<4sII',b'glTF',2,12+8+len(encoded)+8+len(binary))+struct.pack('<I4s',len(encoded),b'JSON')+encoded+struct.pack('<I4s',len(binary),b'BIN\0')+binary
(MODELS/'Purr2D2_Hilt.glb').write_bytes(glb)

# Deterministic native CPU rasterizer, for geometry/material inspection only.
# Per-vertex GGX-like broad specular response plus a depth buffer. No GPU needed.
WIDTH=1000;HEIGHT=1200;image=Image.new('RGB',(WIDTH,HEIGHT));pixels=image.load()
for y in range(HEIGHT):
    val=round(13+7*(1-abs(y-HEIGHT*.48)/(HEIGHT*.6)))
    ImageDraw.Draw(image).line((0,y,WIDTH,y),fill=(val,val+3,val+7))
eye=(.31,-.39,.11);target=(0,0,-.107);forward=unit(sub(eye,target));right=unit(cross((0,0,1),forward));up=cross(forward,right)
scale=4100;screen=[];depth=[];colors=[]
lights=[(unit((.6,-.7,.7)),(1.2,1.35,1.5)),(unit((-.8,.25,.4)),(1.3,.8,.4)),(unit((.3,.8,-.2)),(.4,.65,.9))]
for p,n,band in zip(positions,normals,vertex_bands):
    rel=sub(p,target)
    screen.append((WIDTH*.5+dot(rel,right)*scale,HEIGHT*.49-dot(rel,up)*scale));depth.append(dot(rel,forward))
    base,rough,metal=PALETTE[band];base=[(c/255)**2.2 for c in base]
    result=[c*.22 for c in base]
    for direction,light in lights:
        nl=max(0,dot(n,direction));half=unit(tuple(a+b for a,b in zip(direction,forward)))
        nh=max(0,dot(n,half));power=max(8,100*(1-rough)**2)
        spec=nh**power*(.8+.5*metal)
        for c in range(3):
            f0=.04*(1-metal)+base[c]*metal
            result[c]+=light[c]*(base[c]*nl*(1-.65*metal)+spec*f0)
    colors.append(tuple(min(255,round(255*(v/(1+v))**(1/2.2))) for v in result))
zbuffer=[-1e30]*(WIDTH*HEIGHT)
for i in range(0,len(indices),3):
    a,b,c=indices[i:i+3];p,q,r=screen[a],screen[b],screen[c]
    area=(q[0]-p[0])*(r[1]-p[1])-(q[1]-p[1])*(r[0]-p[0])
    if abs(area)<1e-9:continue
    xlo=max(0,math.floor(min(p[0],q[0],r[0])));xhi=min(WIDTH-1,math.ceil(max(p[0],q[0],r[0])))
    ylo=max(0,math.floor(min(p[1],q[1],r[1])));yhi=min(HEIGHT-1,math.ceil(max(p[1],q[1],r[1])))
    for y in range(ylo,yhi+1):
        for x in range(xlo,xhi+1):
            u=((q[0]-x-.5)*(r[1]-y-.5)-(q[1]-y-.5)*(r[0]-x-.5))/area
            v=((r[0]-x-.5)*(p[1]-y-.5)-(r[1]-y-.5)*(p[0]-x-.5))/area
            w=1-u-v
            if min(u,v,w)<-1e-6:continue
            z=u*depth[a]+v*depth[b]+w*depth[c];offset=y*WIDTH+x
            if z<=zbuffer[offset]:continue
            zbuffer[offset]=z
            pixels[x,y]=tuple(round(u*colors[a][k]+v*colors[b][k]+w*colors[c][k]) for k in range(3))
draw=ImageDraw.Draw(image)
try:
    titlefont=ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf',28)
    smallfont=ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf',18)
except OSError:titlefont=smallfont=ImageFont.load_default()
draw.text((56,53),'PURR2D2  /  FIELD HILT',fill=(190,209,217),font=titlefont)
draw.text((56,95),'Original machined-metal hilt  |  220 mm',fill=(115,147,159),font=smallfont)
draw.text((56,1110),'Cyan activation ring  /  Ribbed grip  /  Brass trim',fill=(138,168,179),font=smallfont)
draw.text((56,1140),f'{triangles:,} triangles  |  One PBR atlas  |  CPU material preview',fill=(90,122,137),font=smallfont)
image.save(ART/'purr2d2-hilt-preview.png')
report={'triangles':triangles,'vertices':len(positions),'mesh':'Purr2D2_Hilt','bounds':document['accessors'][0],
        'material':'Purr2D2_Hilt_Material','textures':'1024 x 1024 PNG','units':'meters','authoring':'original portable procedural generator','blend_saved':False}
(ART/'purr2d2-hilt-metadata.json').write_text(json.dumps(report,indent=2)+'\n')
print('WROTE',MODELS/'Purr2D2_Hilt.glb',ART/'purr2d2-hilt-preview.png',flush=True)
