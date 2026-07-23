import base64, json, hashlib
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

SECRET = "change-this-to-a-very-long-random-secret-key"
TOKEN = "rijz955KA-WqiEYCzwKUYX_COxXd-WgD0RmogElaQDGTHPUPl9xH5iK5HjGsNv1u1MrYnCs2k0qQCHToU-jEYzIn5UDAtpf6jF6_X-4sPt205qC_-kYOYtE9HmoSQ2PrVQkvBCUo7HNzFPnZyZms0i9qP3TZ5lw41hLeRV_88wqvfkrcmFX6pf2UsSkUbO4ppP0ftl-xwSKW2EIq_eLUNvx1VHkZEYxKJEq5V39AHnaQdAIvmBDTTVVvcDglLL3STogJZFugP_CrPhRmGQC86-oTs_98vtbo3IlYo0r3te6Ttgl_ze97BkRvEkR35jUYVRhW2Zd99LgN0HqFgZIIfbes9W4GvPHaPNc_FWQZlUd6wGuaR13Ar3KHPpq3kuGoW5gzMVmPSAAYkjqxwu2c27dMbpYKqa4TEhxLdA0sszYCz6rPw01qGpvUbWNKNQYv"

key = hashlib.sha256(SECRET.encode()).digest()
data = base64.urlsafe_b64decode(TOKEN + "==")
nonce, ciphertext = data[:12], data[12:]
signed_jwt = AESGCM(key).decrypt(nonce, ciphertext, None).decode()

payload = json.loads(base64.urlsafe_b64decode(signed_jwt.split(".")[1] + "=="))
print(json.dumps(payload, indent=2))
