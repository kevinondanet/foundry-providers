from pathlib import Path
import re
import html
import json
import hashlib
from reportlab.pdfgen import canvas
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak, Table, TableStyle, Flowable
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
SOURCE = HERE / 'report-source.md'
OUT = ROOT / 'output/pdf/microsoft-foundry-evaluation-provider-layer.pdf'
OUT.parent.mkdir(parents=True, exist_ok=True)
pdfmetrics.registerFont(TTFont('Arial', '/System/Library/Fonts/Supplemental/Arial.ttf'))
pdfmetrics.registerFont(TTFont('ArialBold', '/System/Library/Fonts/Supplemental/Arial Bold.ttf'))
pdfmetrics.registerFont(TTFont('CourierNew', '/System/Library/Fonts/Supplemental/Courier New.ttf'))
pdfmetrics.registerFontFamily('Arial', normal='Arial', bold='ArialBold', italic='Arial', boldItalic='ArialBold')
NAVY = colors.HexColor('#152D46')
TEAL = colors.HexColor('#087E8B')
INK = colors.HexColor('#263747')
MUTED = colors.HexColor('#607184')
PALE = colors.HexColor('#F0F5F8')
RULE = colors.HexColor('#D6E1E8')
WIDTH = 516
styles = {
    'body': ParagraphStyle('body', fontName='Arial', fontSize=10, leading=14.4, textColor=INK, spaceAfter=9),
    'h1': ParagraphStyle('h1', fontName='ArialBold', fontSize=22, leading=26, textColor=NAVY, spaceAfter=14),
    'h2': ParagraphStyle('h2', fontName='ArialBold', fontSize=12.5, leading=16, textColor=NAVY, spaceBefore=8, spaceAfter=8, keepWithNext=True),
    'cell': ParagraphStyle('cell', fontName='Arial', fontSize=9, leading=12.5, textColor=INK),
    'th': ParagraphStyle('th', fontName='ArialBold', fontSize=9, leading=12, textColor=colors.white),
    'ref': ParagraphStyle('ref', fontName='Arial', fontSize=9.1, leading=12.2, textColor=INK, spaceAfter=9),
    'meta': ParagraphStyle('meta', fontName='Arial', fontSize=9, leading=12, textColor=MUTED, spaceAfter=12),
}
raw = SOURCE.read_text()
refs = {}
for n, line in re.findall(r'^\[(\d+)\] (.+)$', raw, re.M):
    match = re.search(r'\]\((https://[^)]+)\)', line)
    refs[n] = match.group(1) if match else '#ref' + n

def rich(value):
    value = html.escape(value, quote=False)
    value = re.sub(r'\[([^\]]+)\]\((https://[^)]+)\)', lambda m: f'<a href="{m[2]}" color="#087E8B">{m[1]}</a>', value)
    value = re.sub(r'`([^`]+)`', r'<font name="CourierNew" size="9">\1</font>', value)
    value = re.sub(r'\*\*([^*]+)\*\*', r'<b>\1</b>', value)
    def cite(m):
        nums = re.findall(r'\d+', m[1])
        return '[' + ', '.join(f'<a href="{refs[n]}" color="#087E8B">{n}</a>' for n in nums) + ']'
    return re.sub(r'\[(\d+(?:,\s*\d+)*)\]', cite, value)

class TargetJudge(Flowable):
    def __init__(self):
        super().__init__()
        self.width = WIDTH
        self.height = 108
    def draw(self):
        c = self.canv
        def box(x,y,w,title,sub):
            c.setFillColor(PALE)
            c.setStrokeColor(RULE)
            c.roundRect(x,y,w,37,5,fill=1,stroke=1)
            c.setFillColor(NAVY)
            c.setFont('ArialBold',9)
            c.drawCentredString(x+w/2,y+22,title)
            c.setFont('Arial',8)
            c.setFillColor(MUTED)
            c.drawCentredString(x+w/2,y+9,sub)
        def arrow(x1,y1,x2,y2):
            c.setStrokeColor(TEAL)
            c.setFillColor(TEAL)
            c.setLineWidth(1.3)
            c.line(x1,y1,x2,y2)
            p=c.beginPath()
            p.moveTo(x2,y2)
            if x1 == x2:
                p.lineTo(x2-3,y2+5);p.lineTo(x2+3,y2+5)
            else:
                p.lineTo(x2-5,y2+3);p.lineTo(x2-5,y2-3)
            p.close();c.drawPath(p,fill=1,stroke=0)
        box(0,8,152,'CANDIDATE CLIENT','Its provider and deployment')
        box(181,8,150,'RESPONSE TO SCORE','Messages, tools, usage')
        box(360,8,156,'EVALUATOR','Rubric and score')
        box(360,64,156,'JUDGE CLIENT','Separate fixed deployment')
        arrow(153,26,178,26)
        arrow(332,26,357,26)
        arrow(438,63,438,48)
        c.setFont('Arial',8)
        c.setFillColor(MUTED)
        c.drawString(0,83,'Conceptual .NET model-based evaluation flow [12]')

def header(c,doc):
    c.saveState()
    c.setFillColor(TEAL)
    c.rect(48,755,30,3,stroke=0,fill=1)
    c.setFillColor(MUTED)
    c.setFont('Arial',8)
    c.drawString(86,753,'RESEARCH BRIEF  |  08 SEP 2026')
    c.setStrokeColor(RULE)
    c.setLineWidth(.5)
    c.line(48,47,564,47)
    c.setFont('Arial',8)
    c.setFillColor(MUTED)
    c.drawString(48,33,'MICROSOFT FOUNDRY  /  PROVIDER & EVALUATION RESEARCH')
    c.drawRightString(564,33,f'{doc.page} / 5')
    c.restoreState()

story=[]
for page_index, section in enumerate(raw.split('<!-- pagebreak -->')):
    if page_index:
        story.append(PageBreak())
    blocks=re.split(r'\n\s*\n', section.strip())
    for block in blocks:
        if block.startswith('# '):
            story.append(Paragraph(rich(block[2:]),styles['h1']))
        elif block.startswith('## '):
            story.append(Paragraph(rich(block[3:]),styles['h2']))
        elif block.startswith('| '):
            rows=[r for r in block.splitlines() if not re.match(r'^\|[\s:|\-]+\|$',r)]
            data=[]
            for idx,row in enumerate(rows):
                cells=[]
                for cell in row.strip('|').split('|'):
                    content=rich(cell.strip())
                    if page_index==0:
                        content=content.replace('Microsoft.Extensions.AI.Evaluation','Microsoft.Extensions.AI.<br/>Evaluation')
                    if page_index==1:
                        content=content.replace('a separate IChatClient','a separate<br/>IChatClient')
                    cells.append(Paragraph(content,styles['th' if idx==0 else 'cell']))
                data.append(cells)
            widths=[139,189,188] if page_index==0 else [134,197,185]
            table=Table(data,colWidths=widths,hAlign='LEFT',repeatRows=1)
            commands=[('BACKGROUND',(0,0),(-1,0),NAVY),('VALIGN',(0,0),(-1,-1),'TOP'),('TOPPADDING',(0,0),(-1,-1),9),('BOTTOMPADDING',(0,0),(-1,-1),9),('LEFTPADDING',(0,0),(-1,-1),9),('RIGHTPADDING',(0,0),(-1,-1),9),('LINEBELOW',(0,0),(-1,-1),.5,RULE)]
            for row in range(1,len(data)):
                commands.append(('BACKGROUND',(0,row),(-1,row),PALE if row%2 else colors.white))
            table.setStyle(TableStyle(commands))
            story.extend([table,Spacer(1,11)])
        elif block.startswith('<!-- diagram:'):
            story.extend([TargetJudge(),Spacer(1,7)])
        elif re.match(r'^\[\d+\]',block):
            n=re.match(r'^\[(\d+)\]',block)[1]
            story.append(Paragraph(f'<a name="ref{n}"/>'+rich(block),styles['ref']))
        else:
            style=styles['meta'] if block.startswith('Research brief |') else styles['body']
            story.append(Paragraph(rich(block.replace('\n',' ')),style))

doc=SimpleDocTemplate(str(OUT),pagesize=(612,792),rightMargin=48,leftMargin=48,topMargin=56,bottomMargin=60,title='Does Microsoft provide the model-provider layer?',author='Research prepared for the InspectAzureAI project',subject='Microsoft Foundry provider abstractions and evaluation frameworks')
doc.build(story,onFirstPage=header,onLaterPages=header)
reader=PdfReader(OUT)
assert len(reader.pages)==5, 'Recheck fixed page total after layout changes'
texts=[p.extract_text() for p in reader.pages]
links=sum(len(p.get('/Annots',[])) for p in reader.pages)
qa={'artifact':str(OUT),'pages':len(reader.pages),'source_sha256':hashlib.sha256(raw.encode()).hexdigest(),'page_word_counts':[len(t.split()) for t in texts],'link_annotations':links,'source_references':len(refs),'visual_review':'pending'}
(HERE/'validation.json').write_text(json.dumps(qa,indent=2))
(HERE/'extracted-text.txt').write_text('\n\n--- PAGE ---\n\n'.join(texts))
print(json.dumps(qa,indent=2))
