import {createCipheriv} from 'node:crypto';
import {writeFileSync} from 'node:fs';
const key=Buffer.alloc(32,0x42),nonce=Buffer.from('000102030405060708090a0b','hex');
const aad=Buffer.from('PersonalOrderDesk|R0|synthetic-book|1');
const plaintext=Buffer.from('{"amount_cents":"9007199254740993","note":"中文测试"}');
const cipher=createCipheriv('aes-256-gcm',key,nonce);cipher.setAAD(aad);
const ciphertext=Buffer.concat([cipher.update(plaintext),cipher.final(),cipher.getAuthTag()]);
writeFileSync('next/contracts/aes-gcm-vector.json',JSON.stringify({purpose:'SYNTHETIC TEST ONLY; NEVER USE THIS KEY OR NONCE IN PRODUCTION',algorithm:'AES-256-GCM',key_hex:key.toString('hex'),nonce_hex:nonce.toString('hex'),aad_hex:aad.toString('hex'),plaintext_hex:plaintext.toString('hex'),ciphertext_and_tag_hex:ciphertext.toString('hex')},null,2)+'\n');
