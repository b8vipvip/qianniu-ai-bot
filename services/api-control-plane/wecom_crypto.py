from __future__ import annotations

import base64
import secrets
import struct
from typing import Callable

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


WECOM_BLOCK_SIZE = 32


def pkcs7_pad_32(value: bytes) -> bytes:
    padding = WECOM_BLOCK_SIZE - (len(value) % WECOM_BLOCK_SIZE)
    return value + bytes([padding]) * padding


def pkcs7_unpad_32(value: bytes) -> bytes:
    if not value:
        raise ValueError("企业微信回调明文为空")
    padding = value[-1]
    if padding < 1 or padding > WECOM_BLOCK_SIZE:
        raise ValueError("企业微信回调填充长度无效")
    if value[-padding:] != bytes([padding]) * padding:
        raise ValueError("企业微信回调填充内容无效")
    return value[:-padding]


def decode_aes_key(encoding_aes_key: str) -> bytes:
    try:
        key = base64.b64decode((encoding_aes_key or "") + "=")
    except Exception as exc:
        raise ValueError("EncodingAESKey不是有效的Base64值") from exc
    if len(key) != 32:
        raise ValueError("EncodingAESKey解码后必须是32字节")
    return key


def decrypt_message(encrypted: str, encoding_aes_key: str, expected_receive_id: str) -> str:
    key = decode_aes_key(encoding_aes_key)
    raw = base64.b64decode(encrypted)
    decryptor = Cipher(algorithms.AES(key), modes.CBC(key[:16])).decryptor()
    plain = pkcs7_unpad_32(decryptor.update(raw) + decryptor.finalize())
    if len(plain) < 20:
        raise ValueError("企业微信回调明文长度无效")
    msg_len = struct.unpack("!I", plain[16:20])[0]
    message_end = 20 + msg_len
    if message_end > len(plain):
        raise ValueError("企业微信回调消息长度无效")
    receive_id = plain[message_end:].decode("utf-8", errors="strict")
    if expected_receive_id and receive_id != expected_receive_id:
        raise ValueError("企业微信回调接收方ID不匹配")
    return plain[20:message_end].decode("utf-8", errors="strict")


def encrypt_message(message: str, encoding_aes_key: str, receive_id: str) -> str:
    key = decode_aes_key(encoding_aes_key)
    message_bytes = (message or "").encode("utf-8")
    plain = (
        secrets.token_bytes(16)
        + struct.pack("!I", len(message_bytes))
        + message_bytes
        + (receive_id or "").encode("utf-8")
    )
    padded = pkcs7_pad_32(plain)
    encryptor = Cipher(algorithms.AES(key), modes.CBC(key[:16])).encryptor()
    encrypted = encryptor.update(padded) + encryptor.finalize()
    return base64.b64encode(encrypted).decode("ascii")


def install_on_bridge(bridge: object) -> None:
    def decrypt_callback(encrypted: str) -> str:
        return decrypt_message(
            encrypted,
            getattr(bridge, "WECOM_CALLBACK_AES_KEY"),
            getattr(bridge, "WECOM_CORP_ID"),
        )

    def encrypt_callback(message: str) -> str:
        return encrypt_message(
            message,
            getattr(bridge, "WECOM_CALLBACK_AES_KEY"),
            getattr(bridge, "WECOM_CORP_ID"),
        )

    setattr(bridge, "decrypt_callback", decrypt_callback)
    setattr(bridge, "encrypt_callback", encrypt_callback)
