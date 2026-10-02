#!/bin/bash
# Restarts the cvhub systemd service on the VPS.
# Uses passwordless sudo (ubuntu is in the sudo group on the Oracle Cloud image).
set -e
ssh -i /home/tangent/.ssh/oracleInstance.key -o ConnectTimeout=10 ubuntu@129.154.39.177 "sudo systemctl restart cvhub.service"
echo "cvhub.service restarted"
