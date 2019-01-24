using Pulumi;
using System;
using System.Text;
using Pulumi.Azure.Core;
using Pulumi.Azure.Compute;
using Pulumi.Azure.Network;

class Program
{
    static void Main(string[] args)
    {
        Runtime.Run(() => {
            var config = new Config();
            var username = config.Require("username");
            var password = config.Require("password");

            var resourceGroup = new ResourceGroup("server", new ResourceGroupArgs() {
                Location = "West US"
            });

            var network = new VirtualNetwork("server-network", new VirtualNetworkArgs() {
                ResourceGroupName = resourceGroup.Name,
                Location = resourceGroup.Location,
                AddressSpaces = new IO<IO<string>[]>(new IO<string>[] {"10.0.0.0/16"}),
                // Workaround two issues:
                // (1) The Azure API recently regressed and now fails when no subnets are defined at Network creation time.
                // (2) The Azure Terraform provider does not return the ID of the created subnets - so this cannot actually be used.
                Subnets = new IO<VirtualNetworkArgsSubnet>[] {
                    new VirtualNetworkArgsSubnet() { Name = "default", AddressPrefix = "10.0.1.0/24" }
                }
            });

            var subnet = new Subnet("server-subnet", new SubnetArgs() {
                ResourceGroupName = resourceGroup.Name,
                VirtualNetworkName = network.Name,
                AddressPrefix = "10.0.2.0/24"
            });

            var publicIP = new PublicIp("server-ip", new PublicIpArgs() {
                ResourceGroupName = resourceGroup.Name,
                Location = resourceGroup.Location,
                PublicIpAddressAllocation = "Dynamic"
            });
            
            var networkInterface = new NetworkInterface("server-nic", new NetworkInterfaceArgs() {
                ResourceGroupName = resourceGroup.Name,
                Location = resourceGroup.Location,
                IpConfigurations = new IO<NetworkInterfaceArgsIpConfiguration>[] {
                    new NetworkInterfaceArgsIpConfiguration() {
                        Name = "webserveripcfg",
                        SubnetId = subnet.Id,
                        PrivateIpAddressAllocation = "Dynamic",
                        PublicIpAddressId = publicIP.Id,
                    }
                }
            });

            var userData = @"
                #!/bin/bash
                echo ""Hello, World!"" > index.html
                nohup python -m SimpleHTTPServer 80 &
            ";

            var vm = new VirtualMachine("server-vm", new VirtualMachineArgs(){
                ResourceGroupName = resourceGroup.Name,
                Location = resourceGroup.Location,
                NetworkInterfaceIds = new IO<string>[] { networkInterface.Id },
                VmSize = "Standard_A0",
                DeleteDataDisksOnTermination = true,
                DeleteOsDiskOnTermination = true,
                OsProfile = new VirtualMachineArgsOsProfile() {
                    ComputerName = "hostname",
                    AdminUsername = username,
                    AdminPassword = password,
                    CustomData = userData,
                },
                OsProfileLinuxConfig = new VirtualMachineArgsOsProfileLinuxConfig() {
                    DisablePasswordAuthentication = false,
                },
                StorageOsDisk = new VirtualMachineArgsStorageOsDisk() {
                    CreateOption = "FromImage",
                    Name = "myosdisk1",
                },
                StorageImageReference = new VirtualMachineArgsStorageImageReference() {
                    Publisher = "canonical",
                    Offer = "UbuntuServer",
                    Sku = "16.04-LTS",
                    Version = "latest",
                }
            });

            // The public IP address is not allocated until the VM is running, so we wait
            // for that resource to create, and then lookup the IP address again to report
            // its public IP.
            Runtime.Export("publicIp",
                vm.Id.SelectMany(id =>
                    publicIP.Name.SelectMany(name =>
                        publicIP.ResourceGroupName.SelectMany<string>(resourceGroupName =>
                            NetworkModule.GetPublicIP(new GetPublicIPArgs() {
                                Name = name,
                                ResourceGroupName = resourceGroupName,
                            }).Select(ip => ip.IpAddress)
                        )
                    )
                )
            );
        });
    }
}
