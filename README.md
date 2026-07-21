# More Realistic (And Safer) Atmospherics

## Project Objectives

Make atmospherics system more intuitive and fun to engage with by making the machines more closely follow realistic behavior.  This has the effect of making machines tend to be easier to understand and safer to use, as well as introduces interesting and compelling engineering problems to solve.

## Changes

### Passive Pressure Regulators

In real life, pressure regulators are mechanical devices that do not require power.

This mod changes pressure & backpressure regulators to always be considered "powered".  You can still plug them into a data network to control them, in which case they will consume 5 watts.

Performance-wise, regulators now function a lot more like one-way valves. Their throughput is much higher than before, but they've lost the capability to generate a higher pressure on the output than the input.  If you want to generate a pressure differential, you will need a pump.

### Pressure Differential Caps

In real life, it takes more and more energy to pump gasses from a lower to higher pressure.  The higher the differential, the more torque your pump needs in order to continue operating at full speed.

By default, every device in Stationeers is capable of generating effectively infinite pressure differentials with no cost to energy or efficiency.  This introduces serious gameplay and design problems, such as the fact that an uncontrolled volume pump or gas mixer, given enough input gas, will inevitably burst its output network.

This mod changes several atmospherics devices so that as they approach their pressure differential maximum, their throughput falls off, with a hard cap of 0 at their maximum differential.

As an example, a volume pump now has a maximum differential of 10 MPa.  If it has 5 MPa on the input network, then the maximum it is capable of pumping to on the output is 10 MPa.

#### Devices Changed

- Volume Pump - 10 MPa
- Turbo Volume Pump - 40 MPa
- Gas Mixer - 5 MPa
- Advanced Furnace - 40 MPa
- Air Conditioner - 5 MPa
- Filtration Unit - 5 MPa
- Active - 5 MPa
- Powered Vent - 10 MPa
- CombustionCentrifuge - 5 MPa

#### Not Altered

- Electrolyzer - Already uses MoveToEqualize logic
- H2Combustor - Already uses MoveToEqualize logic
- Nitrolyzer - Has a hard lock to prevent output from exceeding internal pressure
