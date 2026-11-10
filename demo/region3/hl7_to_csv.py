"""Convert this demo's HL7 2.5.1 ORU messages to MERIT's configured CSV layout.

This converter supports the generated fixtures only; it is not a general clinical HL7 interface.
Usage: python demo/region3/hl7_to_csv.py demo/region3/source_a.hl7
"""
import csv
from pathlib import Path
import sys

HEADER = 'PatientLast,PatientFirst,DOB,Sex,Street,Apt,City,State,Zip,County,LOINC,Result,Units,SampleType,Method,Collected,Reported,Accession,Status,LabCLIA'.split(',')

def iso(value):
    return f'{value[:4]}-{value[4:6]}-{value[6:8]}'

def convert(path):
    rows = []
    message = {}
    for segment in Path(path).read_text(encoding='ascii').splitlines():
        fields = segment.split('|')
        kind = fields[0]
        if kind == 'MSH':
            if message:
                raise ValueError('Previous message did not contain a specimen segment.')
            if fields[8] != 'ORU^R01^ORU_R01' or fields[11] != '2.5.1':
                raise ValueError('Expected a demo ORU_R01 message, HL7 2.5.1.')
        message[kind] = fields
        if kind != 'SPM':
            continue
        if not all(key in message for key in ['MSH','PID','OBR','OBX','SPM']):
            raise ValueError('Incomplete demo HL7 message.')
        msh,pid,obr,obx,spm = (message[key] for key in ['MSH','PID','OBR','OBX','SPM'])
        name = pid[5].split('^')
        address = pid[11].split('^')
        if obx[2]!='NM' or spm[4].split('^')[0]!='BLDV' or obx[3].split('^')[0]!='77307-7':
            raise ValueError('Only the demo numeric venous blood lead observations are supported.')
        rows.append([name[0],name[1],iso(pid[7]),pid[8],address[0],address[1],address[2],address[3],address[4],address[8][-3:],
                     obx[3].split('^')[0],obx[5],obx[6].split('^')[0],'V',obx[17].split('^')[0],iso(obr[7]),iso(obr[22]),
                     obr[3].split('^')[0],obx[11],msh[3]])
        message = {}
    if message:
        raise ValueError('Incomplete final demo message.')
    return rows

if __name__ == '__main__':
    source = Path(sys.argv[1])
    destination = Path(sys.argv[2]) if len(sys.argv)>2 else source.with_suffix('.csv')
    rows = convert(source)
    with destination.open('w',newline='',encoding='utf-8') as f:
        writer = csv.writer(f)
        writer.writerow(HEADER)
        writer.writerows(rows)
    print(f'{len(rows)} observations converted to {destination}')
